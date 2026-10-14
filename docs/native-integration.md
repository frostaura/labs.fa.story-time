# Native AI integration (Phase C + TTS overhaul) — wiring notes

Written by the native-AI workstream. This file tells the orchestrator exactly how the
real engines plug into the MAUI heads without this workstream touching `MauiProgram.cs`.

## What exists

| Piece | Where | Status (2026-07-30, TTS overhaul) |
|---|---|---|
| llama.cpp binding | `src/app/TaleWeaver.Core/Engines/Llama/LlamaNative.cs` (pinned tag `b10189`, commit `b2f2216`; header snapshot in `Engines/Llama/vendor/`) | Working on macOS (Metal) via Harness |
| Story engine | `src/app/TaleWeaver.Core/Engines/LlamaStoryEngine.cs` — Qwen3-1.7B Q4_K_M, GBNF grammars (`Contracts/Grammars/outline.gbnf`, `poster.gbnf`), age-banded prompts in `Engines/Llama/LlamaPrompts.cs` | Working (harness weave passes) |
| TTS engine | `src/app/TaleWeaver.Core/Engines/Sherpa/SherpaTtsEngine.cs` — sherpa-onnx + **Kokoro v1.0** (`kokoro-multi-lang-v1_0`, fp32 quality tier; engine auto-falls-back to `model.int8.onnx` low-storage tier). Prosody-aware assembly: `Prosody.cs` (punctuation pauses 0.42/0.48/0.62 s, paragraph beat 0.85 s, comma-clause 0.18 s, speed 0.90 ± 0.02 deterministic per sentence) + `AudioShaper` (edge trim, 12/28 ms fades, −1 dBFS peak normalize). Personas: **Ember=af_heart(3), Willow=af_bella(2), Sage=am_michael(16)**; legacy v0_19 map auto-selected when the bundle has ≤11 speakers | Working on macOS (RTF ≈0.33 warm), Android emulator (real weave), **iOS simulator (static link, QA self-test)** |
| TTS native seam | `Engines/Sherpa/SherpaTtsNative.cs` — `ISherpaTtsNative` + managed-NuGet default backend; `SherpaTtsEngine.NativeFactory` is the platform hook | iOS installs its backend via ModuleInitializer |
| iOS sherpa binding | `src/app/TaleWeaver.App/Platforms/iOS/SherpaNativeIos.cs` — `DllImport("__Internal")` over the 6 offline-TTS C functions, exact v1.13.4 struct layout | Sim-verified (`qa-tts.json` ok=true, 8.0 s audio); device arm64 links 0 errors |
| iOS TTS QA self-test | `Platforms/iOS/SherpaQaSelfTest.cs` — launch with `SIMCTL_CHILD_TALEWEAVER_QA_TTS=1`; writes `Documents/qa-tts.{wav,json}` | Debug-only, inert without the env var |
| Paragraph-aware narration | `SentenceSplitter.SplitScript` → `NarrationScript` (Core/Abstractions) → `ITtsEngine.SynthesizeAsync(script, …)` default-interface overload; `StoryPipeline` passes the script; players keep using `StoryPipeline.SplitSentences` (same segmentation, count-consistent) | Wired |
| Model resolution | `Platforms/Android/ModelProvider.cs`, `Platforms/iOS/ModelProvider.cs` — AppData → bundle/asset-pack → `#if DEBUG` LAN bootstrap. iOS default host = **Mac's Bonjour name baked at Debug build** (`Native.props` target → assembly metadata `TaleWeaver.ModelServerDefault`); `model-server.txt` overrides. iOS logs download progress every 64 MB and exposes `IosModelProvider.DownloadProgress` (fileName, bytes, total) for the Weaving screen | Emulator + iOS sim bootstrap proven end-to-end |
| Native libs | `~/TaleWeaverNative/{osx-arm64,android-arm64,ios}` via `scripts/build-native.sh`; iOS now also carries `sherpa-onnx.xcframework` + `onnxruntime.xcframework` (STATIC, prebuilt v1.13.4 — `build-native.sh sherpa-ios`, sha-pinned) wired by `TaleWeaver.App/Native.props` | Staged + linking |
| Models | `~/TaleWeaverModels` via `scripts/fetch-models.sh` (SHA256-pinned; `--with-fallback` adds Qwen3-0.6B; `--tts-int8` adds/packs the int8 TTS tier) | Fetched + verified |

## DI registration lines for MauiProgram.cs (orchestrator wires these)

The TTS overhaul makes iOS identical to Android — **the `#if ANDROID`/`#else` split around
`ITtsEngine` should collapse to one registration**. Current MauiProgram registers
`PlatformTtsEngine` directly on iOS; replace with:

```csharp
builder.Services.AddSingleton<PlatformTtsEngine>();          // stays: it is the gated fallback
#if ANDROID || IOS
builder.Services.AddSingleton<ITtsEngine, ModelGatedTtsEngine>();   // was ANDROID-only
#else
builder.Services.AddSingleton<ITtsEngine>(sp => sp.GetRequiredService<PlatformTtsEngine>());
#endif
```

`ModelGatedTtsEngine` (Services/ModelGatedEngines.cs) is already `#if ANDROID || IOS` and
resolves lazily on first use (no startup download). No other registration changes: the iOS
static binding self-registers via ModuleInitializer (`SherpaNativeIos`), as does
`LlamaNativeResolver`. `IosModelProvider` now takes an optional `ILogger<IosModelProvider>`
— container satisfies it automatically.

Optional (Weaving screen): subscribe `IosModelProvider.DownloadProgress` to surface
first-run model download progress; Android's provider still logs only.

Notes for the orchestrator:

- The availability check is cheap ONLY when models are already present. In Debug with no
  models it triggers the LAN bootstrap download (GBs) — `ModelGatedTtsEngine`/`ModelGatedStoryEngine`
  already defer resolution to first weave, keep it that way.
- `LlamaStoryEngine` and `SherpaTtsEngine` are `IDisposable` singletons; dispose order
  matters on-device (architecture §11: pipeline handles LLM-before-TTS ordering; app
  shutdown should dispose both).
- Voice narrator-picker samples are cached per `SherpaTtsEngine.VoiceSampleVersion`
  (`<voice>-v2.wav`); bump the constant when voices/prosody change again.

## TTS model tiers (founder decision 2026-07-30: quality outranks size)

| Tier | Bundle | Model file | On-device size | When |
|---|---|---|---|---|
| Quality (default) | `kokoro-multi-lang-v1_0` (fp32) | `model.onnx` (326 MB) | ≈377 MB | Default everywhere; Mac RTF ≈0.33 |
| Low-storage | same dir name, int8 contents | `model.int8.onnx` (114 MB) | ≈165 MB | `fetch-models.sh --tts-int8` packs the device tar from it; flip if phone RTF/storage demands |

Both tiers ship under the same directory name; `SherpaTtsEngine` prefers `model.onnx` and
falls back to `model.int8.onnx`, so the tier is a packaging decision, not a code path.
`TALEWEAVER_TTS_DIR` overrides the harness bundle for A/B listening.

## Environment

- `TALEWEAVER_NATIVE_ROOT` (default `~/TaleWeaverNative`) — consumed by both Native.props.
- `TALEWEAVER_MODEL_ROOT` (default `~/TaleWeaverModels`) — harness + scripts.
- `TALEWEAVER_LLM` — harness-only override to point at the 0.6B fallback GGUF.
- `TALEWEAVER_TTS_DIR` — harness-only override for the TTS bundle directory (tier A/B).
- `TALEWEAVER_QA_TTS=1` — iOS Debug: run the sherpa self-test at launch (`SIMCTL_CHILD_` prefix on sim).

## Rebuild recipes

```bash
scripts/build-native.sh macos       # harness dylibs (Metal)
scripts/build-native.sh android     # libllama.so + libggml*.so (arm64-v8a, stripped)
scripts/build-native.sh ios         # llama.xcframework (slow; runs upstream build-xcframework.sh)
scripts/build-native.sh sherpa-ios  # sherpa-onnx + onnxruntime STATIC xcframeworks (prebuilt, sha-pinned)
scripts/fetch-models.sh --with-fallback          # fp32 TTS quality tier + LLMs
scripts/fetch-models.sh --tts-int8               # add int8 TTS tier + pack device tar from it
scripts/serve-models.sh             # LAN model server for device Debug bootstrap
```

## iOS QA recipe (simulator)

```bash
scripts/serve-models.sh &                                        # models on :8787
dotnet build src/app/TaleWeaver.App/TaleWeaver.App.csproj \
  -f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64         # bakes <mac>.local:8787
xcrun simctl install <udid> .../TaleWeaver.App.app
SIMCTL_CHILD_TALEWEAVER_QA_TTS=1 xcrun simctl launch <udid> net.frostaura.taleweaver
# poll: <data-container>/Documents/qa-tts.json → {"ok":true, ...}; listen to qa-tts.wav
```
