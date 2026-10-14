# StoryTime — Architecture overview

**Binding contract for all implementation work.** When this doc and code disagree, stop and resolve the drift (spec-driven mandate). Design contract: [`../design/mockup-spec.md`](../design/mockup-spec.md). Copy rules: [`../design/copy-glossary.md`](../design/copy-glossary.md). Build and run: [`../../README.md`](../../README.md).

## Solution layout

```
fa.story-time.slnx
src/
  shared/StoryTime.Contracts        # net10.0, zero deps. DTOs only: ChildProfile, StoryOutline,
                                    #   PosterSpec (contract v3), StoryRecord, StoryBible,
                                    #   MakingProgress, NarrationManifest (sentence → timestamp map)
  app/StoryTime.Core                # net10.0, no MAUI. Owns everything testable:
    Abstractions/                   #   IStoryEngine, ITtsEngine, IStoryMakingHost, IMakingBackgroundAnchor,
                                    #   INarrationPlayer, IPremiumStore, IStoryCatalog, INavigator, …
    Pipeline/                       #   StoryPipeline (4 stages, checkpoint/resume, progress),
                                    #   CooldownService, SafetyFilter, MakingTelemetry
    Engines/AppleFoundation/        #   AppleFoundationStoryEngine, FoundationPrompts, ProseGuards,
                                    #   PosterSpecMapper, IFoundationModelsBridge
    Engines/Narration/              #   SoothingNarrator chain: segmentation, pause map, mastering, WAV
    Engines/                        #   TemplateStoryEngine (deterministic, always works), Prompts/
    Audio/                          #   SoftHum, AmbientComposer (Premium bed), NarrationPad
    Persistence/                    #   SqliteStoryStore (profiles, stories, bible, checkpoints)
    Services/                       #   StoreEntitlements, PremiumLedger, StoryEngineStatus, WelcomeStoryFactory, …
    ViewModels/                     #   one per screen; CommunityToolkit.Mvvm
    ServiceCollectionExtensions.cs  #   AddStoryTimeCore(options) — TryAdd* so the platform head wins
  app/StoryTime.Poster              # net10.0 + SkiaSharp, no MAUI: procedural parallax poster engine
  app/StoryTime.App                 # MAUI iOS head (net10.0-ios, iOS 27.0). XAML + Shell.
    Platforms/iOS/                  #   AppleFoundationModelsBridge, AppStoreBridge, MakingBackgroundCoordinator,
                                    #   NativeAudioIntegration, Narration/, SceneDelegate
    Services/                       #   AppleSpeechTtsEngine, StoryMakingHost, SoftHumService, players, BiometricGate
    Views/, Controls/, Resources/   #   pages per mockup-spec; MakingSheetHost; ParallaxPosterView
  native/StoryTimeNative            # Swift framework: FoundationModels/ (SEFMEngine) and Store/ (SESKStore)
  native/StoryTimeNative.Binding    # .NET for iOS binding of that framework
  native/lab                        # macOS lab: the real C# engine against the same Swift shim
  tools/StoryTime.NarrationLab      # narration chain experiments on the Mac
tools/StoryTime.PosterLab           # poster contact sheets and goldens on the Mac
tests/StoryTime.Core.Tests          # xUnit against Core (includes BannedWordGuardTests)
tests/StoryTime.Poster.Tests        # poster engine tests and goldens
```

## Cross-cutting decisions (locked)

1. **On-device only.** No server and no account. The only network use is the App Store for the Premium purchase. No model files are bundled or downloaded: every model is one Apple ships with the OS.
2. **Engine seams.** `IStoryEngine` (outline / scene / polish / poster-spec / repair), `ITtsEngine` (sentences → audio file + timestamps) and the poster engine. Swap points are `RegisterStoryEngine` and `RegisterTtsEngine` in `MauiProgram.cs`. `TemplateStoryEngine` and `PlatformTtsEngine` are the permanent fallbacks.
3. **Text: Apple Foundation Models.** `AppleFoundationStoryEngine` drives Apple's on-device model through the `StoryTimeNative` Swift shim (`SEFMEngine`, JSON in / JSON out). The C# side owns prompts, budgets and the failure ladder: guided outline with an exact scene count and a short story bible, each scene streamed in a fresh session carrying only the running summary, optional per-scene polish (off by default), and template fallback once more than two scenes fail. A device that can never run the model gets the template engine outright; otherwise availability is re-checked per story, so turning Apple Intelligence on takes effect on the next story. Detail: [`../research/foundation-models.md`](../research/foundation-models.md), [`../research/genai-lab-fm-shim.md`](../research/genai-lab-fm-shim.md).
4. **Narration: Apple speech, shaped.** `AppleSpeechTtsEngine` renders each utterance with `AVSpeechSynthesizer` to PCM, then the SoothingNarrator chain in `Core/Engines/Narration` applies the measured pause map, mastering and loudness (−18 LUFS / −2 dBTP) and a soft pad 28 LU under the voice. Output: one 48 kHz WAV per story plus a `NarrationManifest` of per-sentence timestamps. The teaser (first ~3 sentences) renders first. One storyteller, Serena (Premium voice, downloaded by the parent in the storyteller-voice step), reads every story. Detail: [`../research/narration.md`](../research/narration.md), [`../research/voice-lab.md`](../research/voice-lab.md).
5. **Posters: procedural.** The story engine emits only an enum-constrained `PosterSpec` (contract v3); `StoryTime.Poster` decides everything visual from the spec and a seed, deterministically (same spec → same pixels). Four depth layers, rasterized once with bleed; parallax is a translation of the prerendered layers from neutral-referenced device tilt. A flattened thumbnail per story. Detail: [`../design/poster-engine.md`](../design/poster-engine.md).
6. **Pipeline.** Stages Planning → Writing → Picture → Voice (internally Outline → Scenes·stitch·polish → Poster → Narration), progress weights 5/40/5/50, checkpoint after every stage, scene, window and sentence to SQLite, local notification on completion. The wait between stories is 30 minutes (Premium 15), from a persisted timestamp.
   **Hosting.** `StoryMakingHost` owns the pipeline task; staying alive outside the foreground is delegated to `IMakingBackgroundAnchor` (`Platforms/iOS/MakingBackgroundCoordinator`), called on every start and every exit path, in three layers: (1) while the soft hum is audibly playing on the `Playback` / `SpokenAudio` session with `UIBackgroundModes: audio`, the app keeps running and the story run continues; (2) if the hum is off, a `BeginBackgroundTask` assertion is taken on backgrounding, the granted budget is logged rather than assumed, and on expiry the run is cancelled with a checkpoint (never discarded); (3) a `BGProcessingTaskRequest` (`net.frostaura.storytime.story-resume`) resumes the checkpointed run if iOS grants it — opportunistic, promised in no copy. Resume on launch is the floor under all three.
   **Scheduling strategies.** The pipeline picks one of two schedules per story run and stamps it into the checkpoint (a resume continues the schedule it started under):
   - **Overlapped** (default on devices with ≥ 6 GB RAM and ≥ 6 cores) — producer–consumer. Text is polished per ~2-scene window as scenes complete (`IStoryEngine.PolishWindowAsync`; only the final window may write the goodnight close) and handed to a channel; narration consumes windows in order while later scenes are still being written. The poster runs after the final window's text work, in parallel with the remaining narration. Sentence indices and timestamps stay globally exact across windows. Progress is the weighted sum of all four stage fractions; the reported stage is the furthest started, so both are monotone.
   - **Sequential** (low-memory fallback, or forced via `StoryTimeOptions`) — all scenes, then whole-story polish, poster, narration. Also the resume path for legacy checkpoints. The thresholds were tuned for the previous bundled-model engines and are due a re-measure on the Apple engines.
7. **Safety.** Age-banded prompts (3–4 / 5–6 / 7–8) → guided generation for structured stages → `SafetyFilter` lexicon check per scene → bounded repair (`RepairPassageAsync`) → template fallback. `ProseGuards` rewrites unwanted model wording. Kid free text never enters a prompt; Story Bible writes pass the same filter. The content-filter level (Gentle / Brave-ish) widens only the allowed theme palette; every hard layer is identical at both levels, and ages 3–4 are always Gentle.
8. **Persistence.** sqlite-net-pcl, single `storytime.db3` in AppData; audio and poster PNGs under `AppData/media`, the DB stores paths. The Storage row sums real bytes.
9. **Entitlements.** One non-consumable StoreKit 2 purchase through the Swift shim (`SESKStore`), bound behind `IPremiumStore`; every transaction JWS-verified, recorded locally in `PremiumLedger`; a `Transaction.updates` listener starts at launch. Seven free nights from first run.
10. **Analytics.** None. Removed entirely by founder decision 2026-07-31 — no toggle, no settings member, no sink; a reflection test keeps it that way.
10b. **Audio session and native players.** One shared session, three tenants, one owner of the system surface.
   - **Session:** category `Playback` with `AllowAirPlay | AllowBluetoothA2DP`, mode `SpokenAudio`, configured through one configurator by both narration and the soft hum. `PlayAndRecord` is forbidden — it routes to the earpiece.
   - **Narration owns the system surface:** `MPNowPlayingInfoCenter` (title, storyteller, poster artwork, duration, elapsed, rate) and `MPRemoteCommandCenter` play / pause / toggle / stop / seek / ±15 s against the same player. Next / previous are disabled.
   - **The beds never own it:** the soft hum and the Premium ambient bed write no metadata, so the lock screen and AirPlay always show the story.
   - **Route loss pauses narration, never the story run.**
   - **The live-TTS fallback publishes nothing** and clears any stale entry: its timeline is estimated.
11. **Memory.** No model weights live in the app process; the Foundation Models runtime is the OS's. The overlapped strategy keeps text generation and narration co-resident by design.
12. **Shell map.** Onboarding (3 pushed pages plus the storyteller-voice step, first run only) → `//home`; `player` presented modally; flat routes `shelf`, `settings`, `library`, `premium`, `content-filters`, `storyteller-voice` — no route name collides with the root tab route. The making sheet is an overlay on Home, not a page. QA env vars (`STORYTIME_QA_ROUTE`, `STORYTIME_QA_MAKE`, `STORYTIME_QA_NARRATION`, `STORYTIME_QA_ANY_VOICE`) cold-start routes and runs.

## Definition of done (per feature)

Compiles with zero warnings · Core logic unit-tested · runs on the iOS simulator (and on device where the feature is device-only) · screenshot compared against the design contract · copy checked against the glossary · `docs/known-gaps.md` updated if anything is stubbed or unproven.
