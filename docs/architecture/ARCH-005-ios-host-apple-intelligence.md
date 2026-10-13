# ARCH-005 — iOS 27+ Host with Apple Intelligence and On-Device Dictation

## Summary
StoryTime ships natively on **iOS 27+ only**. A thin SwiftUI host (`src/ios`) loads the StoryTime web app in a `WKWebView` and exposes a native bridge. That bridge drafts story text with **Apple Intelligence** (the Foundation Models framework) and transcribes speech with the Speech framework's **`SpeechAnalyzer` + `DictationTranscriber`**, entirely on-device. **Android is not a build target.**

## Context & Problem
- Story text was composed server-side, either by deterministic templates or by the optional OpenRouter pipeline (ARCH-003). Remote LLM calls cost money, add latency, and send child-facing prompts off-device.
- Shipping our own on-device models would mean multi-GB downloads, model lifecycle management, and per-platform runtimes.
- Keeping feature parity between iOS and Android is expensive while the Apple-native capabilities above have no equivalent shared stack.

## Decision
- **Platform:** iOS/iPadOS 27.0 deployment target. Android targets are removed and out of scope until parity is re-planned. The browser PWA stays available as the cross-platform fallback.
- **Generation:** the host checks `SystemLanguageModel.default.availability`. When the model is `available`, it runs a `LanguageModelSession` with guided generation (a `@Generable` draft containing a title and 1–8 scenes). The web app posts that draft to `POST /api/stories/generate` as `onDeviceDraft` with `provider: "apple-foundation-models"`.
- **API remains authoritative:** the backend still owns tier policy, cooldowns, poster and narration media, approval, Story Bible continuity, and the library. It accepts a draft only when `StoryTime:Generation:OnDeviceDraft:Enabled=true` and the provider is allow-listed. It trims and caps the draft (scene count by duration, plus title and scene character limits). If the draft is missing, unusable, or rejected, it composes server-side (ARCH-003).
- **Dictation:** `DictationTranscriber` with the `.progressiveShortDictation` preset streams volatile and final results. The host relays partial results to the page as `storytime:dictation` DOM events and resolves the `startDictation` call with the final transcript. Speech assets are installed by the OS through `AssetInventory`; the app bundles and downloads no models.
- **Bridge contract:** `window.webkit.messageHandlers.storyTime.postMessage({ action })` is a `WKScriptMessageHandlerWithReply`, so each call returns a Promise. Actions are `capabilities`, `draftStory`, `startDictation`, and `stopDictation`. The TypeScript side is `src/frontend/src/services/nativeBridge.ts`; the Swift side is `StoryTimeKit/BridgeProtocol.swift`.
- **Graceful degradation:** outside the host, or when Apple Intelligence is unavailable or throws (for example a guardrail violation), the web app sends no draft and hides dictation controls.

## Alternatives Considered
- Downloadable open-weight models (llama.cpp/MLC) on both platforms: large downloads, thermal cost, and two native runtimes to maintain.
- Keeping OpenRouter as the primary path: recurring cost and off-device prompts.
- A full native SwiftUI rewrite: duplicates the tested web UI. The WKWebView host keeps one UI and one test suite.

## Consequences
- Zero-cost, private, offline-capable text generation on eligible devices.
- Dictation works offline with system-managed language assets.
- Devices without Apple Intelligence fall back to server composition transparently.
- The iOS build requires Xcode 27 / iOS 27 SDK on macOS CI runners.

## Affected Components
- `src/ios/project.yml`, `src/ios/StoryTime/*`, `src/ios/StoryTimeKit/*`
- `src/frontend/src/services/nativeBridge.ts`, `src/frontend/src/App.tsx`, `src/frontend/src/components/QuickGenerateCard.tsx`
- `src/backend/StoryTime.Api/Contracts/GenerateStoryRequest.cs`, `src/backend/StoryTime.Api/Services/StoryGenerationService.cs`, `src/backend/StoryTime.Api/StoryTimeOptions.cs`
- `.github/workflows/ci.yml` (`ios` job), `Makefile` (`ios-test`, `ios-build`)
