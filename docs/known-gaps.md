# Known gaps

Honest catalogue of what is stubbed, unproven or environment-gated in StoryTime today. Update whenever a gap opens or closes; delete a row once it is closed (git history keeps it). Gaps carried over from the previous app were re-checked against this codebase on 2026-10-14; rows that applied only to its Android head, bundled models or server bootstrap were dropped.

## Product and store

| Gap | State | Closes when |
|---|---|---|
| Premium product not yet in App Store Connect | StoreKit 2 purchase, restore and the `Transaction.updates` listener are built and testable against `StoryTime.storekit` in Xcode; until the founder creates `net.frostaura.storytime.premium`, the Premium page says Premium can't be bought right now (README → Premium) | Founder creates the product and submits it with the first build |
| Name clearance | "StoryTime" has not been cleared as a trademark or App Store name | Before any public artifact |
| "Continue the series · +2 min" | Design-state stub: shows a short "Making 2 more minutes… ✓" state, then resets; no extra scene is actually made or narrated | Coda-scene implementation |
| Soft hum and App Store guideline 2.5.4 | Background audio that also keeps work alive is what 2.5.4 targets. Assessed moderate: the hum is a genuine audible feature the parent can hear and switch off, and checkpoint/resume is the fallback — but a reviewer may read it as a keep-alive | Stage-7 compliance gate. Fallback if rejected: drop `UIBackgroundModes: audio`, keep the hum foreground-only, rely on checkpoint/resume |
| Pad vs Premium music bed | The narration pad is baked into every story at −46 LUFS (28 LU under the voice); the Premium ambient bed plays on top when enabled. They can clash harmonically, and a free pad may blur the Premium "music under every voice" pitch | Product call: pad for all, Premium-only, or replaced by the bed |
| Settings Storage row | Shows real on-device bytes; read-only, no manage view behind it | Later scope |

## Background making (iOS)

| Gap | State | Closes when |
|---|---|---|
| Background making depends on the soft hum | While the hum is audibly playing on the `Playback`/`SpokenAudio` session the story run keeps going in the background. Silence it (music slider ≈ 0, Control Center stop, a call or Siri) and the app falls back to a `BeginBackgroundTask` grace window, a clean checkpoint and resume on return. Copy must never promise background making unconditionally | Accepted design; revisit only if the hum is cut |
| `BGProcessingTask` resume is unproven | `net.frostaura.storytime.story-resume` is registered at launch, permitted in `Info.plist` and submitted on backgrounding mid-story. The simulator refuses to schedule it, and on a device iOS decides if and when it runs. Promised nowhere in the copy, by design | Physical-iPhone overnight test (simulate the launch under a debugger, then a real night) |
| Grace-window length is not a constant | The `BeginBackgroundTask` budget is the system's; `MakingBackgroundCoordinator` logs the measured seconds rather than assuming ~30 s. No device number recorded yet | Physical-iPhone run with the music slider at zero |
| Background completion unproven | Background survival has been seen; a story finishing, and its ready notification firing, while the app stays in the background has not | `STORYTIME_QA_MAKE=1` cold start on an idle Mac, or the physical iPhone |
| Soft hum is metric-audible, not ear-verified | The hum clears an audible floor by construction (`SoftHum.MinAudibleVolume` 0.12, slider × 0.5 capped at 0.5; a unit test asserts the rendered peak). Nobody has listened to it through this build | Founder listen on the phone |
| Strategy thresholds not re-measured | The overlapped-vs-sequential guard (≥ 6 GB RAM and ≥ 6 cores) was tuned for the previous bundled-model engines. iOS registers no platform `IDeviceCapabilities`, so Core's GC and processor-count defaults decide | Device benchmark of both strategies on the Apple engines |

## Narration and audio

| Gap | State | Closes when |
|---|---|---|
| Serena (Premium) not heard through the app on a phone | The chain is verified by unit tests, two rounds of independent objective audio review on Mac renders (compact voices — no Serena on the Mac) and an iOS-simulator render (`STORYTIME_QA_NARRATION=1`). The founder heard Serena through the lab recipe, not through this build | Device pass with Serena installed: onboarding detects her, the sample plays, a 10-minute story renders; read the `StoryNarration` log lines |
| Narration speed on device unmeasured | Mac JIT ≈ 75× real time; iOS simulator (Debug) ≈ 8–13×. `StoryTime.Core` is AOT-compiled on device (`MtouchInterpreter all,-StoryTime.Core`); that build is unexercised on hardware | Device run; read the `StoryNarration phase …` timings |
| Narration files are uncompressed | 48 kHz mono 16-bit WAV ≈ 5.8 MB per minute (a 10-minute story ≈ 58 MB). `IAudioEncoder` exists but only passes through | AAC encode after assembly (AVAudioConverter → m4a) |
| Scene changes inside an overlapped window are paragraph pauses | Only explicit scene markers (`***`, `⁂`, `~`, two blank lines) and window seams get the 2.2 s scene pause; polished text joins scenes with a single blank line | Story engines emit a scene marker between scenes |
| Live-TTS fallback has no native transport | The no-voice fallback speaks through `AVSpeechSynthesizer` live: no seekable item, no honest timeline, and a remote pause lands only between sentences, so it publishes no Now Playing entry and clears any stale one | Naturally closes whenever the rendered-narration path is available |
| Ambient bed not ear-verified on iOS | The `AVAudioPlayer` loop under narration builds and mirrors the intended duck and fades, but simultaneous mixing has not been listened to on a phone | Physical-iPhone QA pass |
| Ambient bed render cost on phone silicon | ~0.4 s steady-state on M-series Macs; no phone number. The bed fades in when the render lands and never blocks narration | Device benchmark |
| Now Playing, AirPlay and CarPlay unobserved on hardware | `MPNowPlayingInfoCenter` and `MPRemoteCommandCenter` are wired and build; route loss pauses. No AirPlay receiver, CarPlay head unit or Bluetooth speaker has been in the loop | Physical-device pass with an AirPlay speaker and a car or Bluetooth device |

## UI and platform

| Gap | State | Closes when |
|---|---|---|
| Biometric gate on simulator | Falls back Face ID → device passcode (the real `LAContext` path runs on the simulator); an enrolled-Face-ID match cannot be simulated on this Xcode | Physical-device QA |
| Editing `Info.plist` alone does not rebuild the app manifest | The incremental iOS build does not treat the source `Info.plist` as an input, so a plain rebuild can ship the old plist. Workaround: full rebuild (`-t:Rebuild`) and verify with `plutil -p <app>/Info.plist` | Upstream fix in Microsoft.iOS.Sdk |
| Animated Shell pops | On MAUI 10.0.60 animated pops from a pushed stack were seen never to complete; `ShellNavigator` uses non-animated pops, so the gentle pop transition is lost | Retest on the next MAUI servicing release |
| Player modal swipe-down close | Approximated with drag-handle tap, a swipe-down recognizer and system back; not an interactive drag-to-dismiss sheet | Polish pass |
| Making sheet drag-down is threshold-based | Collapse commits on a ≥ 110 pt drag; no velocity or fling detection, and only the handle area drags | Polish pass |
| Mini-bar absent on the Premium page | `PremiumPage` is modal and covers the bar (same as the Player, by design) | Accepted — modal pages cover overlays |
| Pressed states rely on `TouchBehavior` | Library rows, Shelf tiles, Settings rows and the Player's play disc take their pressed state from CommunityToolkit `TouchBehavior`; the iOS press animation is code-parity, not eye-verified | Physical-iPhone QA pass |
| `GhostButton` style still exists | Unused for real actions but kept, so a stray use degrades gracefully instead of failing to resolve | Delete once no XAML references it |
| Parallax neutral pitch is fixed | The vertical tilt channel is referenced to a ~41° reading angle (0.75 g). A parent reading lying down, or with the phone flat, gets roll-only response. Deliberate: a self-calibrating baseline reads as the autonomous drift the founder rejected | Only if the fixed neutral is reported as wrong in practice |
