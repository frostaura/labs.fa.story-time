# Foundation Models for story generation (research, 2026-10-14)

Desk research against the Xcode 27 SDK's `FoundationModels.swiftinterface` (module 2.0.68, cited
[SDK27]) plus Apple docs and WWDC sessions. The empirical lab results live in
[`genai-lab.md`](genai-lab.md) when present. **Measure on device before trusting any number
marked unverified.**

## Hard facts

- **Binding gap.** `Microsoft.iOS` 26.5 has no FoundationModels namespace. All calls go through a
  Swift `@objc` shim, built with Xcode 27 for an iOS 26.0 deployment target. Gate iOS 27 APIs with
  `if #available(iOS 27, *)`.
- **Availability.** `SystemLanguageModel.default.availability` is `.available` or
  `.unavailable(.deviceNotEligible | .appleIntelligenceNotEnabled | .modelNotReady)`.
  - `supportsLocale(_:)` exists.
  - No public deep link opens the Apple Intelligence settings, so the app shows instructions
    instead ("Settings › Apple Intelligence & Siri").
  - `SystemLanguageModel` is Observable: watch it and retry while the state is `modelNotReady`.
- **Context window.** 4,096 tokens per session (TN3193). On iOS 27, `contextSize` is read at
  runtime; the WWDC26 sample prints 8192. **Design for 4,096.** Everything counts toward it:
  instructions, prompt, schema, `@Guide` text and the response.
  - Measure with `tokenCount(for:)` (iOS 26.4+).
  - On iOS 27, read `response.usage` / `session.usage`.
- **Overflow.** The error is `GenerationError.exceededContextWindowSize` on iOS 26 and
  `LanguageModelError.contextSizeExceeded` on iOS 27. Either way the session is dead afterwards.
- **Guided generation.**
  - Macros: `@Generable` and `@Guide`.
  - Guides: `.anyOf`, `.pattern`, `.range`, `.minimum`/`.maximum`, `.count(n|a...b)`,
    `.minimumCount`/`.maximumCount`, `.element`.
  - Fields are generated in declaration order, so put a plan or reasoning field first.
  - `DynamicGenerationSchema` handles runtime schemas.
  - `GeneratedContent.jsonString` serializes the result, which is what crosses the bridge.
  - Every schema costs prompt tokens, so prose is a plain String stream and only the outline,
    digest and poster are `@Generable`.
- **Options.** `GenerationOptions(samplingMode:temperature:maximumResponseTokens:)`.
  - Sampling modes: `.greedy`, `.random(top:seed:)`, `.random(probabilityThreshold:seed:)`.
  - Temperature takes values 0–1 per the current docs.
  - `maximumResponseTokens` is a runaway guard only, because it truncates mid-sentence.
- **Guardrails.** Both input and output are checked.
  - Violations throw `GenerationError.guardrailViolation` on iOS 26 and
    `LanguageModelError.guardrailViolation` on iOS 27.
  - `.permissiveContentTransformations` only affects String output, so **do not use it**: we are
    generating content for children, not transforming input.
  - Refusals in guided generation throw `.refusal` with an `explanation`.
  - Refusals in String generation look like normal text, so detect them with a regex plus the
    lexicon filter.
  - A plain re-run often passes. Opening the instructions with a role and audience reduces
    over-blocking.
- **Adapters** are obsoleted in iOS 27. Use case `.general`.
- **iOS 27 additions.**
  - `SystemLanguageModel.variant` (`.core3` / `.coreAdvanced3`).
  - `ContextOptions(reasoningLevel:)` and `transcriptErrorHandlingPolicy`.
  - `LanguageModelError`, which adds `.timeout`, `.rateLimited(resetDate)` and
    `.unsupportedLanguageOrLocale`.
  - `SystemLanguageModel.Error.assetsUnavailable`.
  - **Catch both error families.**
- **Concurrency.** One request per session; a second throws `concurrentRequests`. Use a fresh
  session per call. Cancellation is cooperative through `Task.cancel()`.
- **Background.** There is no guarantee generation continues while the app is backgrounded
  (developer reports of `rateLimited`). Generate in the foreground and treat `rateLimited` as
  "resume on foreground".
- **Performance.** No official A18/A19 numbers exist. A 2,000-word story is about 2,700 output
  tokens, estimated at 60–120 s. **Stream scene 1 to the UI while later scenes generate.**

## Shim API (C# binding target)

Completion handlers, not `async`. Each call returns an `NSUUID` request id that `cancel` accepts.
Requests and responses are JSON strings. Error codes are an `Int` enum:

```swift
@objc(SEFMErrorCode) public enum SEFMErrorCode: Int {
  case ok=0, unavailableDevice=1, unavailableNotEnabled=2, unavailableModelNotReady=3,
       unsupportedLocale=4, contextExceeded=10, guardrail=11, refusal=12, rateLimited=13,
       concurrent=14, timeout=15, parse=16, assetsUnavailable=17, cancelled=20, unknown=99
}
@objc(SEFMEngine) public final class SEFMEngine: NSObject {
  @objc public static let shared = SEFMEngine()
  @objc public func availabilityJSON() -> String   // {available, reason, contextSize, variant, localeSupported}
  @objc public func prewarm(stage: String)
  @objc public func countTokens(_ text: String, completion: @escaping (Int, Int) -> Void)
  @objc public func generateOutline(requestJSON: String, completion: @escaping (String?, Int, String?) -> Void) -> UUID
  @objc public func streamScene(requestJSON: String, onDelta: @escaping (String) -> Void,
                                completion: @escaping (String?, Int, String?) -> Void) -> UUID
  @objc public func digestScene(requestJSON: String, completion: @escaping (String?, Int, String?) -> Void) -> UUID
  @objc public func polishScene(requestJSON: String, completion: @escaping (String?, Int, String?) -> Void) -> UUID
  @objc public func generatePoster(requestJSON: String, completion: @escaping (String?, Int, String?) -> Void) -> UUID
  @objc public func cancel(_ requestId: UUID)
}
```

## Stage budgets (4,096 floor, fresh session per call)

Every instruction set opens with the same role line: *"You are a gentle bedtime storyteller for
children aged {band}. Stories are calm, kind and safe. No violence, fear, injury or scary
content."* Age bands are branched in code, never written as IF statements in the prompt.

| Stage | Output | Options | In / out tokens |
|---|---|---|---|
| Outline | `@Generable StoryOutline` (plan, title, setting, 1–4 characters, N beats, theme) + bible ≤10 lines | probThreshold 0.9, seed, temp 0.7, max 900 | ~900 / ~600 |
| Scene k | String stream, ~200 words, continue from last 2 sentences + running summary ≤120 words + character cards + beat | probThreshold 0.9, seed, temp 0.8, max 600 | ~800 / ~400 |
| Digest k | `@Generable SceneDigest` (summary ≤2 sentences, ≤3 new facts) | greedy, max 200 | ~600 / ~120 |
| Polish (per scene, optional) | String, simpler words, same events | temp 0.3, top 20, seed, max 600 | ~700 / ~400 |
| Poster | `@Generable PosterSpec` (enums) | temp 0.5, seed, max 500 | ~700 / ~350 |

Before each call, measure tokens. If the input exceeds `contextSize - max - 200`, trim the summary,
then the bible, never the beat. **Never polish the whole story in one call.**

## Failure ladder (per stage, owned by the C# orchestrator)

1. **Context exceeded:** halve the summary and bible, then retry once.
2. **Guardrail violation:**
   1. Re-run the same prompt once.
   2. If that fails, switch to the pre-tested safer phrasing variant at temp 0.5.
   3. If that fails, use the template engine for this scene.
3. **Refusal:** log the explanation (parent and diagnostics only, never shown to the child), then
   take the same rephrase-then-template path.
4. **Lexicon-filter hit or refusal-regex match on output:** treat it as a guardrail violation. At
   most 2 model attempts per scene.
5. **Rate limited or timeout:** pause and resume on foreground or after `resetDate`, up to 2
   attempts.
6. **Unavailable, assets unavailable or unsupported locale:** use the template engine for the
   whole story. Show guidance only for `appleIntelligenceNotEnabled` and `modelNotReady`.
7. **More than 2 scenes fell back:** re-render the whole story from templates, driven by the
   outline when one exists.

## Unverified

- 8K context on iOS 27.
- What `core3` and `coreAdvanced3` mean.
- Speed on A18/A19.
- Generation while backgrounded.
- How often benign children's prompts trip the guardrail.
- How quickly cancellation takes effect.
- Which error family each OS throws.
- Whether a deep link to the Apple Intelligence settings exists.
- How temperatures above 1 are handled.
