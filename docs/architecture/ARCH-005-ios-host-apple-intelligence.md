# ARCH-005 — iOS 27+ Host with Apple Intelligence, On-Device Narration, and Dictation

## Summary
StoryTime ships natively on **iOS 27+ only**. A thin SwiftUI host (`src/ios`) loads the StoryTime web app in a `WKWebView` and exposes a native bridge. That bridge drafts story text with **Apple Intelligence** (the Foundation Models framework) renders story voiceovers with Apple speech synthesis (**`AVSpeechSynthesizer`**), and transcribes speech with the Speech framework's **`SpeechAnalyzer` + `DictationTranscriber`**, all on-device. **Android is not a build target.**

## Context & Problem
- Story text was composed server-side, either by deterministic templates or by the optional OpenRouter pipeline (ARCH-003). Remote LLM calls cost money, add latency, and send child-facing prompts off-device.
- Shipping our own on-device models would mean multi-GB downloads, model lifecycle management, and per-platform runtimes.
- Keeping feature parity between iOS and Android is expensive while the Apple-native capabilities above have no equivalent shared stack.

## Decision
- **Platform:** iOS/iPadOS 27.0 deployment target. Android targets are removed and out of scope until parity is re-planned. The browser PWA stays available as the cross-platform fallback.
- **Generation:** the host checks `SystemLanguageModel.default.availability`. When the model is `available`, it runs a `LanguageModelSession` with guided generation (a `@Generable` draft containing a title and 1–8 scenes). The web app posts that draft to `POST /api/stories/generate` as `onDeviceDraft` with `provider: "apple-foundation-models"`.
- **API remains authoritative:** the backend still owns tier policy, cooldowns, poster and narration media, approval, Story Bible continuity, and the library. It accepts a draft only when `StoryTime:Generation:OnDeviceDraft:Enabled=true` and the provider is allow-listed. It trims and caps the draft (scene count by duration, plus title and scene character limits). If the draft is missing, unusable, or rejected, it composes server-side (ARCH-003).
- **Narration (voiceovers):** after the API returns a story, the web app calls `narrateStory`. The host renders two AAC (`.m4a`, 32 kbps mono) files with `AVSpeechSynthesizer.write`: a teaser (title plus the opening of the first scene) and the full narration (title, recap, every scene, with calm pauses between segments). It uses the highest-quality (premium/enhanced) installed voice for the user's language. Rate, pitch and pauses follow the story's narration style (for example "whisper" reads slower and softer). Files are cached in Application Support, pruned to the latest 40 stories. Story ids are validated (`[A-Za-z0-9_-]{1,64}`) before touching the file system.
- **Narration storage and approval:** the web app persists only a small reference (`storytime-native:narration/<storyId>/<teaser|full>`) and loads audio on demand through `loadNarration` as a `data:audio/mp4` URI. Inline audio would exceed LocalStorage quotas. The full-narration reference is attached only when the API reports `fullAudioReady` (generation without approval, a successful approve call, or a library sync), so parent approval still gates full playback. If on-device narration fails, the server-provided narration is kept.
- **Dictation:** `DictationTranscriber` with the `.progressiveShortDictation` preset streams volatile and final results. The host relays partial results to the page as `storytime:dictation` DOM events and resolves the `startDictation` call with the final transcript. Speech assets are installed by the OS through `AssetInventory`; the app bundles and downloads no models.
- **Bridge contract:** `window.webkit.messageHandlers.storyTime.postMessage({ action })` is a `WKScriptMessageHandlerWithReply`, so each call returns a Promise. Actions are `capabilities`, `draftStory`, `narrateStory`, `loadNarration`, `startDictation`, and `stopDictation`. The TypeScript side is `src/frontend/src/services/nativeBridge.ts`; the Swift side is `StoryTimeKit/BridgeProtocol.swift`.
- **Graceful degradation:** outside the host, or when Apple Intelligence is unavailable or throws (for example a guardrail violation), the web app sends no draft and hides dictation controls.

## Alternatives Considered
- Downloadable open-weight models (llama.cpp/MLC) on both platforms: large downloads, thermal cost, and two native runtimes to maintain.
- Keeping OpenRouter as the primary path: recurring cost and off-device prompts.
- A full native SwiftUI rewrite: duplicates the tested web UI. The WKWebView host keeps one UI and one test suite.

## Consequences
- Zero-cost, private, offline-capable text generation on eligible devices.
- Voiceovers are real speech with Apple's natural voices instead of the procedural server clips, and are rendered offline at no cost.
- Dictation works offline with system-managed language assets.
- Devices without Apple Intelligence fall back to server composition transparently.
- The iOS build requires Xcode 27 / iOS 27 SDK on macOS CI runners.

## Affected Components
- `src/ios/project.yml`, `src/ios/StoryTime/*`, `src/ios/StoryTimeKit/*`
- `src/frontend/src/services/nativeBridge.ts`, `src/frontend/src/App.tsx`, `src/frontend/src/components/QuickGenerateCard.tsx`
- `src/backend/StoryTime.Api/Contracts/GenerateStoryRequest.cs`, `src/backend/StoryTime.Api/Services/StoryGenerationService.cs`, `src/backend/StoryTime.Api/StoryTimeOptions.cs`
- `.github/workflows/ci.yml` (`ios` job), `Makefile` (`ios-test`, `ios-build`)
