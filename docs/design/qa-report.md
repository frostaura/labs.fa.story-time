# Phase D visual QA report — 2026-07-30

Baseline: commit `416ea25` (integrated, 137/137 tests green). QA against [`mockup-spec.md`](mockup-spec.md).
Devices: Android emulator "Trimix" (1080×2400, API 35, arm64) with tap automation; iPhone 17 Pro simulator (iOS 26.1) screenshot-only (tap automation TCC-blocked — screens reached via `TALEWEAVER_QA_ROUTE` cold launches with a seeded data container).
Screenshots: [`qa-shots/android/`](qa-shots/android/) and [`qa-shots/ios/`](qa-shots/ios/).

## Verdict table

| Screen | Android | iOS |
|---|---|---|
| 1 Onboarding · Profile | **PASS** — palette exact (#201813/#E8A857/#32271D spot-checked), copy verbatim, defaults per doctrine (age 5, Foxes/Ocean/Magic), age select + interest toggle + typed entry all work | **PASS** — parity incl. amber caret + entry focus; sim hardware-keyboard badge in shot is simulator chrome, not app |
| 2 Onboarding · Voice & length | **PASS** — Willow select, track tap → 5, series/one-shot switch with both verbatim mode hints | **PASS** — parity; ▶ rings already text-presentation |
| 3 Onboarding · Rules | **PASS** — numerals amber, all copy verbatim, whisper block, footer | **PASS** — parity |
| 4 Home | **PASS-with-notes** — welcome story "The Lantern Bear" present after ANR fix (below); greeting time-aware; cooldown CTA state verified ("The loom rests — next weave at 16:54"). Note: every list row shows amber ▶; spec's mockup shows it on the first row only | **PASS-with-notes** — same ▶-on-every-row note; ▶ was colour emoji before fix (below), now amber text |
| 5 Weaving (mid) | **PASS** — real-weave capture: 0% Outline weaving…, then 43% Scenes weaving… with done/weaving/waiting colours per token spec; Android backgrounding copy verbatim | **PASS** — idle-state capture (no weave runnable from cold route); shows the documented iOS reinterpretation copy "Come back anytime — we'll pick up right where we left off." |
| 5b Weaving (done) | **PASS** — 100%, headline swap, all stages Success green, cooldown line with time, teaser CTA | not separately captured (route-only reach; same layout verified via Android) |
| 6 Player | **PASS** — 4-layer poster renders (template + LLM-spec posters), drag handle, eyebrow, hairline progress + thumb, ■/▶(❚❚)/↻ controls, approval CTA, Continue-series outline, Voice/Music + PREMIUM, "Ends gently." footer; live-TTS seek moves; play/pause glyph toggles | **PASS-with-notes** — parity via latest-story fallback; poster renders from spec on iOS; playback not exercisable without taps |
| 7 Kid Shelf | **PASS** — tap on lock does nothing; 2 s hold exits (verified); 2-col grid, headline, footer verbatim; approved story appears, unapproved does not | **PASS-with-notes** — layout parity; tiles empty because the seeded DB references Android thumbnail paths (QA-seeding artifact, not an app defect); lock-hold not testable without taps |
| 8 Parent settings | **PASS** — dev-bypass badge (✓ DEV BYPASS, Success green), trial card "Trial · 7 nights left" + UPGRADE →, toggles work (analytics toggled on→off), Profiles/Content filters/Storage rows with real bytes (108.7 KB), upgrade coming-soon sheet verbatim | **PASS** — parity, real storage bytes (76 KB) |
| 9 Library (QA-route only screen) | not reachable in-app on Android (no entry point — by design; QA env-var route needs a cold launch) | **PASS** — "Every story" list renders all three stories |

Shared checks: hairline dividers everywhere, no cards (only the two spec'd bordered surfaces: trial card + lock pill), Outfit/Quicksand render on both platforms (letterforms compared Android↔iOS — no system-font fallback), status-bar legibility fine, iOS safe areas bleed `BgPhone` edge-to-edge (pixel-verified #201813 at y=10 and y=2600).

## Real-AI weave (Android emulator, Debug LAN bootstrap)

Profile: Mila, age 4, Foxes·Ocean·Magic·Space, Willow, **5 min**, series mode. Server: `scripts/serve-models.sh` (verified serving `Qwen3-1.7B-Q4_K_M.gguf` + `kokoro-en-v0_19.bundle.tar` — filenames agree with `AndroidModelProvider`; no script fix needed).

Timeline (wall clock):
- 16:34:18 — Weave tapped; GGUF download starts (`10.0.2.2:8787`)
- 16:35:13 — 1.107 GB GGUF done (~55 s); kokoro tar (159 MB) fetched + extracted
- 16:37:43 — 43 %, Outline done, Scenes·stitch·polish weaving (Qwen3-1.7B via llama.cpp)
- 16:38 — teaser wav written (Narration teaser-first)
- 16:40:53 — 100 % done. **Total ≈ 6 m 35 s including the 1.26 GB bootstrap; ≈ 5 m 20 s pipeline-only.**

Output story (read in full from the on-device DB):
- **Title:** "The Wishing Starlight" — **Episode 3**, real LLM recap line carried from the previous episode ("Previously: The Moonlit Picnic — everything tastes better shared under the moon.")
- **465 words**, DurationSeconds 165.5; audio `story-4.wav` **7.94 MB** (≈ 2:45 @ 24 kHz mono 16-bit) + `story-4-teaser.wav` 390 KB (≈ 8 s) — real Kokoro narration files, not live TTS
- First lines: "Fen picked a glowing star, soft and bright. / Mila smiled, her eyes big and wide. / She shared the star with Fen. / The star shone brighter than ever before. / Fen laughed, and they both giggled."
- Assessment: coherent, warm, age-appropriate, zero safety flags, ends gently with a sleep scene (covenant honoured). Minor: a character "Luna" appears in one scene only (mild cast drift); repetitive sentence rhythm typical of a 1.7B at this prompt budget.
- Player verified against the files: teaser plays (0:05/0:08 observed), approval unlocks, full story plays, tap-seek jumps correctly (1:54/2:45 after ~68 % tap), pause/play glyph toggles.
- **Honest gap:** a 5-minute target produced a 2:45 story. Length calibration (scene count / tokens-per-scene vs. target) needs a pass before M2 is judged.

## Defects found and fixed this session

1. **Deterministic first-run ANR on Home** (`src/app/TaleWeaver.App/Views/HomePage.xaml.cs`). `BreatheAsync`'s `while` loop awaited `ScaleTo`, which completes *synchronously* when the button's platform view is detached — exactly the state right after onboarding pops back to Home. The loop then spun the UI thread with zero yields: main thread pegged in ValueAnimator add/cancel (ANR trace), story list never got a draw pass (this was also the real cause of known-gaps' "Home list first reveal" row), Kid Shelf tap ANR'd. Fix: unconditional ≥600 ms yield floor per iteration. Verified: first-run Home now renders the welcome story immediately and never wedges.
2. **Debug LAN model bootstrap silently dead on Android** (`Platforms/Android/MainApplication.cs`). Android blocks cleartext HTTP by default; the bootstrap's `HttpClient` threw instantly, the catch swallowed it, and every weave fell back to the template engine with models present on the LAN. Fix: `[Application(UsesCleartextTraffic = true)]` under `#if DEBUG` only — Release keeps the platform default (offline-only invariant untouched). Verified: 1.26 GB bootstrap + real weave ran end-to-end.
3. **▶ rendered as blue colour emoji on iOS** (Home rows, Player play glyph, Weaving CTA). Known gotcha (U+FE0E); `OnboardingVoicePage` already carried the fix but four call sites didn't. Fixed with `▶&#xFE0E;` / `"▶︎"` in `HomePage.xaml`, `PlayerPage.xaml`, `PlayerPage.xaml.cs`, `WeavingPage.xaml`. Verified on re-capture.
4. **Player QA route opened an empty player** (`TaleWeaver.Core/ViewModels/PlayerViewModel.cs`). Cold-opening `player` without launch context rendered a blank shell. Fix: fall back to the newest playable story when no context is set (in-app navigation always sets context first). 137/137 Core tests still green after the change.

## Defects remaining / notes

- **Home "Recent & favorites" shows ▶ on every row**; spec's mockup shows it on the first row only. Cosmetic deviation, awkward to express in a `BindableLayout` template — either fix or record as a deliberate reinterpretation in mockup-spec.
- **Weaving page shows stale done-state copy after approval** — returning to the Weaving page after approving still reads "Listen to the teaser first — then it unlocks for the shelf" and the teaser CTA replays the teaser. Harmless but slightly off after approval.
- **5-min target → 2:45 story** (length calibration, above).
- **Kokoro bundle tar contains macOS AppleDouble junk** (`._kokoro-en-v0_19`) which lands in the app's models dir on extraction. Harmless; rebuild the tar with `COPYFILE_DISABLE=1` in `fetch-models.sh`'s bundling step at some point.
- iOS remains screenshot-only QA (TCC); lock-hold, toggles, playback and weave are tap-verified on Android only. Physical-device pass still owed for M2 evidence either way.
- iOS weave/TTS still platform-fallback (no sherpa xcframework) — pre-existing known gap, unchanged.

## Founder feedback pass — 2026-07-30 (same day, post-baseline)

Feedback verbatim: *"The pictures on Android are a visual mess — make no sense. The ones that seem more coherent are pretty much all cats. Nothing relevant to the stories. Episodes should be on a beautiful carousel."* Verdict on the complaint: **justified** — [`06b-player-real.png`](qa-shots/android/06b-player-real.png) shows a giant amber blob (the "cat") covering half the canvas, a floating dark rectangle mid-air, and no grounding or hierarchy.

### What changed

1. **Slot-rule poster composer** (`TaleWeaver.Core/Poster/PosterComposer.cs`). `Validate()` now *repairs* any spec — LLM or template — into the mockup tiles' quiet grammar instead of merely clamping numbers: ≤1 small moon (scale 0.10–0.22, upper third, rule-of-thirds x), one 4–14-dot star sprinkle (upper 60%, never over the moon disc), 1–2 hill ellipses anchored to and overflowing the bottom edge (injected if missing — nothing floats), ≤2 small midground motifs (boat only on water and never behind the focal; lighthouse never sharing a third with the moon or focal, dropped if both thirds are taken), ≤1 foreground focal (fox or lit cottage window) standing on a ground line at a third, ≤4 motifs total besides stars/hills, and exactly one warm amber accent per poster (focal, else lighthouse beam). Repair is deterministic and idempotent; 20 composer unit tests pin the rules.
2. **Painters redrawn** (`TaleWeaver.App/Graphics/SkiaPosterRenderer.cs`). Every shape base-anchors on its ground line. The fox is a new sitting side profile — plump body, chest, ONE pointed snout, two tall close-set ears, large curled tail — unmistakably a fox at 512px and 44px (painter render-dumps reviewed at 512/140/44 during iteration). Hills are soft overflowing ellipses (the mockup's `bottom:-28px` look), water gets 2–3 lighter wave pills, the lighthouse is slim with a gradient-fading beam, WindowGlow is a dark cottage gable with a warm lit window (no more floating rectangles), pines cluster 1–3 by density.
3. **Story relevance.** `TemplateStoryEngine.GeneratePosterSpecAsync` derives motifs from the outline's theme (fox→fox focal, ocean→water+boat/lighthouse, forest→pines, sky→dense stars, cozy→lit window); `LlamaPrompts.PosterUser` now instructs shape-per-zone, story-matched motifs, ≤1 focal + ≤2 supporting (grammar still enum-locks; composer enforces regardless).
4. **Art versioning + regen.** `PosterComposer.ArtVersion` (v4) is stamped into thumbnail filenames; `PosterArtRefresher` sweeps on Home activation and re-renders any story whose thumbnail predates the current version or is missing (it also healed the iOS QA container whose seeded DB pointed at Android paths). Filename-stamping exists specifically because Android's Glide caches by path — re-rendering under the same name serves stale art (observed live this session).
5. **Home carousel** (`Views/HomePage.xaml`). "Recent & favorites" rows → horizontal snapping poster carousel: CollectionView + LinearItemsLayout, `SnapPointsType=MandatorySingle`, 220×300 cards (poster 200h radius 16, title, meta), newest playable first, next-card peek, tap→player, long-press→share/favorite/delete, eyebrow + "All →" (opens Library — its first in-app entry point on Android). CollectionView chosen over CarouselView: CarouselView on MAUI ≥8 carries known position-reset/crash issues, CollectionView snapping is the stabler primitive on 10.0.60. Centered-card scale emphasis deliberately skipped (Hearth: nothing bounces). Recorded as a deliberate reinterpretation in [`mockup-spec.md`](mockup-spec.md). Favorites now show an amber text-glyph ♥ (U+FE0E — the colour-emoji gotcha bit on Android this time) on the meta line here, on Shelf tiles and Library rows.

### Verification (Android emulator, tap-driven; iPhone 17 Pro sim, screenshot)

| Check | Result |
|---|---|
| `dotnet test` | **146/146 green** (was 137; +9 net new poster-rule tests) |
| Home carousel | **PASS** — [`04c-home-carousel.png`](qa-shots/android/04c-home-carousel.png): swipes, snaps to card boundaries, tap opens player, long-press opens context menu (Share/Favorite/Delete verified; Favorite persisted and ♥ renders amber) |
| Player poster (real Qwen3 story) | **PASS** — [`06c-player-poster-v2.png`](qa-shots/android/06c-player-poster-v2.png): same stored LLM spec as 06b now resolves to moon + stars + boat-on-water + amber fox on the ground line |
| Kid Shelf | **PASS** — [`07b-shelf-poster-v2.png`](qa-shots/android/07b-shelf-poster-v2.png): 2-col grid retained; three tiles in three distinct calm palettes; title ellipsis correct |
| Library | **PASS** — [`09b-library-poster-v2.png`](qa-shots/android/09b-library-poster-v2.png): reachable via "All →"; 44px thumbs read cleanly |
| Interest-set variety | 4 regenerated stories span fox/ocean (water+boat+fox), fox/forest (pines+fox on pine-green), cozy (lit cottage+pines on plum), fox/clouds; harness dumps additionally covered ocean/lighthouse and space/starfield |
| iOS | build 0 errors; [`04d-home-carousel-ios.png`](qa-shots/ios/04d-home-carousel-ios.png): carousel + new art parity (screenshot-only per TCC) |

### Honest visual verdict

Before, the player poster was an incoherent amber mass; now every screen reads like the Storyboard tiles: dark quiet field, one small moon, sprinkle of stars, grounded hills, one clear focal. Judged against the four mockup poster tiles, the four live posters hold the bar — composed, grounded, story-flavored, one warm accent. Nits found and fixed *during* this pass by looking at real renders: moon impaled on the lighthouse tower (rule added + tested), boat hidden behind the fox (rule added + tested), red emoji ♥ (U+FE0E), broken title ellipsis in the first card layout. Remaining cosmetic nit, recorded not hidden: after a fling the snapped card can rest a few dp off the exact content-edge alignment (MandatorySingle + Start alignment on 10.0.60); it reads fine and never half-parks a card.

## Overall assessment

Against the "no sloppy demos" bar: **the Hearth contract is faithfully implemented on both heads** — palette hexes are exact, copy is verbatim (including both platform-specific weaving reinterpretations), hierarchy/spacing read like the mockups, and every structural safety affordance (teaser approval, biometric gate, kid-shelf hold-lock, cooldown) actually functions under tap automation on Android. The real on-device pipeline (Qwen3-1.7B + Kokoro, LAN-bootstrapped) produced a coherent, safe, gently-ending episode-3 series story with real narration audio, playable and seekable in the app, in ~5.5 min pipeline time on an emulator. Two genuinely embarrassing defects (first-run ANR; silently dead Debug bootstrap) were found and fixed this session — both are the kind that only shows up by actually driving the app, which is exactly why this pass exists. Remaining items are cosmetic or calibration-level and are listed above without spin.

## Founder UX fix pass — 2026-07-30 (settings crash · slider · haptics · paygate)

Feedback verbatim: *(1) "Tapping on the user icon top-right, the app crashes." (physical iPhone 17 Air) (2) "There doesn't seem to be any way to unlock pro (pay gate page)." (3) "When dragging the slider from 10min to 5, it just pops right back to 10." (4) "Add haptics on things for that perceived performance."*

### Root causes — proven, not guessed

1. **Settings crash on iPhone: missing `NSFaceIDUsageDescription`.** `Platforms/iOS/Info.plist` had no Face ID usage key, and `BiometricGate` evaluated `LAPolicy.DeviceOwnerAuthenticationWithBiometrics` on every settings entry — on a Face-ID device iOS TERMINATES the app at that call (TCC policy, not an exception; unreachable by try/catch). Simulators masked it: no enrolled biometrics → `CanEvaluatePolicy` false → Debug dev bypass, so the crashing call never ran in sim QA. Fix: key added ("TaleWeaver asks Face ID so only a grown-up can open Parent settings."), verified present in the built .app bundle. Defense in depth: gate rewritten to fall back biometrics → `DeviceOwnerAuthentication` (device passcode) → graceful `false`, everything wrapped so the gate can never throw (Android likewise: `BiometricWeak` → `BiometricWeak|DeviceCredential`, no-hardware/no-enrollment → bypass in Debug / quiet false in Release; the locked veil gained a gentle "We couldn't check it's you — try again." line). **Evidence:** [`ios/11-settings-gate-ios.png`](qa-shots/ios/11-settings-gate-ios.png) — the iPhone 17 Pro sim now walks the REAL LAContext path and presents the system "Enter iPhone Passcode for TaleWeaver / Open parent settings" sheet, app alive behind it (dev bypass NOT taken). Enrolled-Face-ID match couldn't be simulated (`simctl ui biometry` unsupported on this Xcode) — physical-device confirmation owed on the founder's phone.
2. **Slider pop-back: MAUI reports `TotalX = 0` on `GestureStatus.Completed`.** The old pan handler recomputed the detent from `panStartX + e.TotalX` on Completed — with `TotalX` reset to 0 that recommitted the ORIGINAL detent, undoing the whole drag (drags also only committed detent-jumps mid-drag, never tracked the finger). Fix: `LengthTrackView` now tracks the finger continuously (thumb + label highlight follow, selection-haptic tick at each detent crossing), remembers the last Running position, and on Completed/Canceled commits the NEAREST detent from that remembered position through the VM gate; an explicit re-snap covers the VM-rejected (premium-gated) case. Snap math extracted to `LengthDetents` in Core (away-from-zero midpoints, unit-tested).

### Premium paygate (new)

`PremiumPage` (modal, Hearth): eyebrow TALEWEAVER · PREMIUM, "Longer nights, softer light.", four hairline benefit rows (15-minute stories · lock-screen art · music under narration · 15-minute rests), and an honest price row — "Coming soon — Premium arrives with the full release. No card, no account, ever." No fake prices; no billing until M4. Debug-only whisper row "unlock for testing" flips `ISettingsStore.DebugPremiumOverride`; `StubEntitlements.IsPremium` honors it (15-min stories allowed, cooldown 15). Entry points routed: Settings UPGRADE → (replaces the coming-soon sheet), the slider gate upsell whisper (now tappable, "…are part of Premium →"), Player Music · PREMIUM row. Settings trial card reads "Premium · unlocked for testing" while overridden. Weaving cooldown line now reads the entitlement minutes instead of a hardcoded "30" (it would have lied under premium).

### Haptics

New Core seam `IHaptics` (Light/Selection/Medium/Success/Warning) + `PlatformHaptics` (iOS UIImpact/UISelection/UINotification generators; Android predefined `VibrationEffect` CLICK/TICK/HEAVY_CLICK/DOUBLE_CLICK via VibratorManager, API-guarded, VIBRATE permission added) + `NullHaptics` for tests/harness. Both platform engines honor OS haptics settings; every call is try/catch no-op-on-error. Wired semantically: CTAs + nav entries + biometric pass + play/pause/replay/teaser (Light) · age/interest/voice/mode/toggles/slider detents (Selection) · weave start (Medium) · weave complete, approval unlock, shelf lock-hold completion (Success, with Light at hold-start) · biometric fail + premium-gate bump (Warning). Nothing on scroll; carousel snap skipped (no reliable snap event — skip rather than spam).

### Verification

| Check | Result |
|---|---|
| `dotnet test` | My scope: 71/71 green (entitlement-override, PremiumViewModel, LengthDetents, Settings routing tests all new). Full suite at run time: 195/196 — the 1 failure is `AudioShaperTests` in the parallel TTS workstream's new code, not this pass's files |
| Android emulator — slider | [`02b-length-drag.png`](qa-shots/android/02b-length-drag.png): 5→10 commits · **10→5 commits (founder's case)** · 5→15 gated (hint + settles back) · 5→15 after dev unlock commits. Same three drags re-verified on onboarding-2 (fresh-install run, app data backed up/restored afterwards) |
| Android — paygate | [`08c-premium-page.png`](qa-shots/android/08c-premium-page.png); all 3 entry points opened it under tap automation (Settings UPGRADE →, slider hint, Player Music row); dev unlock flips to "Premium · unlocked for testing — tap to relock", Settings trial card reflects it, 15-min slider commits, Weaving shows "the loom rests 15 minutes" |
| Android — settings entry | Avatar → gate (DEV BYPASS on emulator, no biometric hardware) → settings render; no crash |
| iOS sim | Both TFMs build 0 errors; `NSFaceIDUsageDescription` verified in built bundle via PlistBuddy; [`ios/10-premium-ios.png`](qa-shots/ios/10-premium-ios.png) premium parity; [`ios/11-settings-gate-ios.png`](qa-shots/ios/11-settings-gate-ios.png) real-LAContext passcode path, no crash |
| Haptics on device | Not directly observable in screenshots; code path exercised on every tap above without crash (incl. Success on weave completion), and emulator reports no vibrator-related errors in logcat |

Physical iPhone: the fixed, signed device build (`ios-arm64`, `NSFaceIDUsageDescription` verified in the bundle) was **installed to the founder's iPhone over wireless devicectl** during this pass; remote launch was blocked only by the lock screen ("device was not, or could not be, unlocked"), so the on-device Face ID prompt itself awaits the founder's next unlock-and-tap.

Residual: physical-iPhone Face ID confirmation (the fix is bundle-verified, installed on the phone, and the sim exercises the real LAContext path — the enrolled-Face-ID prompt itself needs the founder to open Settings once) · haptic *feel* tuning is subjective and untestable in emulation.

## Poster engine v2 — formation contract pass, 2026-07-30 (evening)

Founder feedback verbatim: *"It's very pedestrian. You can build a better procedural engine than that — use the baby LLM to give you a formation JSON you can use to generate the layers."* Bar set: artful flat-illustration night-poster quality (Alto's Odyssey / Monument Valley night scenes), variety between stories, not clipart, not chaos.

### What changed

- **Vocabulary 10 → 31 shapes** (`PosterShape`, additive): celestial (Moon, Crescent, Stars, StarCluster, ShootingStar), atmosphere (Cloud, MistBand, Aurora, Snowfall, FireflyScatter), terrain (Hills, Mountain, ForestRow, Path), Water, flora (Pine, RoundTree, Reeds, FlowerScatter), structures (Lighthouse, WindowGlow, Boat, Sailboat, Lantern, Tent), creatures (FoxSilhouette, Rabbit, Owl, BirdFlock, DeerSilhouette, WhaleTail). New per-element fields `tint` (distance haze toward the sky tone), `glow` (soft radial), `flipX`.
- **Formation contract**: the poster prompt now presents the whole vocabulary WITH semantics per layer (0 sky/atmo · 1 far terrain · 2 midground · 3 foreground focal + framing) plus palette moods; `poster.gbnf` locks the expanded enums/fields (ASCII, 0–3 elements/layer, ≤12 elements keeps the stage inside its 700-token budget). Verified against REAL llama.cpp b10189 + Qwen3-1.7B in a new model-dependent test (`PosterGrammarModelTests`) — grammar parses, samples, and the emitted formation survives the composer.
- **Composer = harmony, not straitjacket** (`ArtVersion` 4 → 7): sane LLM compositions pass through with zone clamps only; repairs fire on violations (boat/sailboat/whale need water and ride the waterline; ≤1 creature; nothing tall under the moon; focal X clamped extent-aware so its whole silhouette stays on the canvas; ≤6 motifs + scatters; warm-lit elements ≤2 = one family; terrain always present, staggered lighter→darker toward the viewer). Moonglade reflection auto-added when moon + water coexist. Deterministic + idempotent, property-tested.
- **Renderer craft**: 3-stop vertical gradient skies; radial glows (moon halo, window, lantern pane, tent door, lighthouse lamp, firefly motes) baked once per prerendered layer; cubic-curve silhouettes for every new painter (deer with antlers, whale flukes, sitting rabbit, tufted owl with moonlit eyes, sailboat with curved sails, mountain twin-peak with snow cap, treeline bands with per-tree jitter); scatters with size/alpha jitter (bright stars get a 4-point sparkle); water reflections as a fading light column; contact shadows ground every standing motif. Parallax travel kept at the design contract [4, 9, 15, 24] px (glow layers deliberately NOT given separate travel — the contract pins 4 layers, and glows ride their owner's layer).
- **TemplateStoryEngine**: per-theme formation builders with 2–3 seeded sub-variants each across fox/ocean/forest/space/clouds/stars/garden/cozy/meadow (+ snowy meadow), so two fox stories no longer produce the same poster. Fixed a latent theme-detection bug found by rendering: `Contains("den")` matched "gar**den**" (all garden stories became fox posters) and "greenhouse of moons" matched the space theme.

### Art-director iteration (4 critique cycles on real renders)

Render-dump harness (scratchpad `posterdump/`, links the real `SkiaPosterRenderer.cs`) rendered every painter isolated (200px/64px contact sheets) + 19 template compositions + 3 LLM-style specs + the chaos-repair spec, at 512/140/44 px, each cycle re-read and critiqued. Fixes that came out of LOOKING, cycle by cycle: MistBand rendered as a hard gray bar (rebuilt as fading lens gradients) · Mountain read as a shark fin (rebuilt as angular twin-peak) · WhaleTail read as a plant (rebuilt to the classic fluke silhouette) · moonglade was a stack of gray blobs (rebuilt as one fading light column) · forest rows too shallow · hills flattened into featureless horizon bands (radius tightened so crests curve) · fireflies floated in the open sky (ground-band clamp) · snow-cap floated over haze-tinted mountains (tint now applies to secondary colors) · flower scatter read as crisp plus-sign noise (soft merged clover blobs) · paths smeared under creatures (paths now only lead to lit dwellings, arriving at the door).

### Verification

| Check | Result |
|---|---|
| `dotnet test` | 195/196 — all composer/grammar/formation tests green incl. the real-model grammar test; the 1 failure is `AudioShaperTests` (parallel TTS workstream's new file, fails identically without this pass's changes) |
| TFM builds | Android 0 errors · iOS sim 0 errors |
| Android emulator (self-contained apk reinstall) | Art regenerated via ArtVersion bump — [`04e-poster-v3.png`](qa-shots/android/04e-poster-v3.png) home carousel, [`06e-poster-v3.png`](qa-shots/android/06e-poster-v3.png) player, [`07e-poster-v3.png`](qa-shots/android/07e-poster-v3.png) kid shelf |

### Honest visual verdict

The dump set is genuinely varied now — 19 template posters with no two alike, and they read at 44 px. The best of them (space-owl with shooting star, lantern-path with fireflies in the treeline, ember tent camp, winter deer under a crescent, cottage lane) I would frame. On device: the shelf's three tiles (fox/lighthouse/moonglade · fox under a cloud-crossed moon · glowing cottage on plum dusk) hold the Alto's-night bar; the player poster (pines, moon, boat, lighthouse, amber fox) is charming and coherent. Remaining honest nits: the re-rendered *stored* spec predates the new vocabulary, so it uses only v1 shapes (correct by design — new art uses the full set); a pine can overlap the moon's lower rim (trees are exempt from moon clearance by choice — tree-before-moon reads as depth, not error); the home carousel's 3:4 crop of square art was clipping edge focals — fixed twice on evidence: first a focal-zone tighten (x 0.22–0.78, ArtVersion 6), then, when the re-rendered card showed a flipped fox's tail still leaving the SQUARE canvas, an extent-aware clamp (per-shape left/right silhouette extents × scale keep the whole shape on the canvas; ArtVersion 7, final). The v3 screenshots above are the ArtVersion-7 renders; a tail tip may still enter the carousel's crop margin — that residual is the "square art, cropping surfaces" gap in known-gaps.md, a View-layer concern out of this pass's scope.

## Premium music pass — 2026-07-30 (procedural ambient bed under narration)

Founder directive: *"best quality and actually appetizing soundtrack"* — fully offline, zero licensing, unique per story, in the same spirit as the procedural posters.

### What shipped

- **`AmbientComposer` (Core/Audio, pure C# DSP).** Renders a 64 s seamless-looping stereo bed at 24 kHz from `{poster palette, mood, seed}` — deterministic per story. Sound design: 5-note pad chords (3 detuned wavetable oscillators per note, ±2.4 cents, raised-cosine 5 s attack / 6 s release crossfades), a gentle i–VI–iv–VII-style 4-chord progression (16 s per chord), sparse beat-quantized pentatonic/chord-tone plucks (4 additive partials, exponential decay) through a ping-pong feedback delay, an optional filtered pink-noise wash (surf/wind, loop-periodic swell LFO), and a master one-pole lowpass breathing on a loop-periodic LFO. Seamlessness by construction: deterministic voices render into loop+12 s tail which folds back onto the head; the stochastic noise is equal-power crossfaded across the seam. Normalized to RMS ≈ −24 dBFS, peak capped at −6 dBFS.
- **Palette → musical-character map** (`AmbientMoodMap`): 12 palettes fold into 6 hand-designed characters — `night-calm` (NightHarbor/StarfieldViolet: Dm7·B♭maj7·Gm7·Cmaj7 dorian), `warm-ember` (EmberSky/LanternGlow: Fmaj7·Dm7·B♭maj7·C9sus), `ocean-waves` (OceanDeep/TidePool: Am9 world, heavy wave-shaped noise, sparser), `forest-night` (MoonMeadow/PineForest: Em9 with wind), `mist-dream` (CloudDrift/MistValley/SnowHush: lydian Cmaj7♯11, airy), `plum-lull` (PlumDusk: Bm9, dusky). The poster mood enum (calm/dreamy/cozy/gentle/wonder/sleepy) nudges tempo/arps/brightness rather than replacing the character.
- **Generation & caching** (`AmbientTrackProvider`): composed lazily off the UI thread at first play, cached as `story-{id}-ambient.wav` beside the narration (temp-write + move); `SqliteStoryStore.DeleteStoryAsync` deletes it with the story.
- **`AmbientPlayer` (App/Services)**: iOS `AVAudioPlayer` (NumberOfLoops −1) on the shared session the MediaElement toolkit already configures (deliberately untouched — both players mix natively); Android `Android.Media.MediaPlayer` (looping, USAGE_MEDIA/CONTENT_TYPE_MUSIC) mixing with MediaElement's ExoPlayer. ~1.5 s fade-in/out, ramped volume changes; `StopAsync` never blocks page close (fade finishes in background).
- **Duck policy (documented choice):** bed = slider × **0.35** while narration speaks; slider × **0.55** while paused-but-open (deliberately NOT full slider — a jump to full on pause would startle at bedtime); fade-out on stop/completed/close. Dragging the Music slider up from silence auditions the bed even before play.
- **Player UI:** mix rows are live `MixTrackView` sliders (Hearth hairline + accent fill + 10px dot; drag pattern adapted from LengthTrackView incl. the TotalX=0-on-Completed lesson). Voice drives `MediaElement.Volume` live (disabled in live-TTS mode via `INarrationPlayer.SupportsVolume`); Music is live for Premium (persisted `MusicVolume`, default 0.3) and keeps the dimmed `PREMIUM` → paygate row otherwise. Recorded as a deliberate reinterpretation in mockup-spec.
- **Harness:** `ambient --palette NightHarbor --seed 7 [--mood calm]` renders a loop and prints peak/RMS/seam/render-time.

### Objective numbers (scipy over 4 rendered moods, seed 7)

| Loop | Peak | RMS | Spectral centroid | Seam Δ vs max in-loop Δ | Clipped samples | DC |
|---|---|---|---|---|---|---|
| nightharbor (night-calm) | −11.6 dBFS | −24.4 dBFS | 198 Hz | 4.4e-3 ≤ 1.9e-2 | 0 | ~1e-9 |
| embersky (warm-ember) | −11.1 dBFS | −24.4 dBFS | 229 Hz | 7.3e-4 ≤ 3.2e-2 | 0 | ~1e-9 |
| oceandeep (ocean-waves) | −11.4 dBFS | −24.4 dBFS | 173 Hz | 7.3e-4 ≤ 1.9e-2 | 0 | ~1e-9 |
| moonmeadow (forest-night) | −11.3 dBFS | −24.4 dBFS | 219 Hz | 2.7e-4 ≤ 2.1e-2 | 0 | ~1e-9 |

All centroids far under the 1.5 kHz mellowness bar; chord movement verified per 16 s segment (dominant pitch walks the progression); 1 s RMS envelope breathes ~7 dB (slow swells, nothing rhythmic); pluck band raised once on evidence (first render buried them at −41 dB — a drone, not appetizing; now discrete events at −35..−39 dB, gentle sparkle). Render: ~0.4 s steady-state on M-series (first-run JIT ~2.6 s). Sample loops (20 s excerpts, ≤2 MB): [`qa-audio/ambient-nightharbor.wav`](qa-audio/ambient-nightharbor.wav), [`qa-audio/ambient-embersky.wav`](qa-audio/ambient-embersky.wav).

### Verification

| Check | Result |
|---|---|
| `dotnet test` | **217/217 green** (was 196; +21 this pass: composer determinism, seed/palette divergence, level bounds, DC, loop-seam continuity ×4 palettes, stereo decorrelation, mood-map totality + anchors, mood tweaks, context parsing, WAV round-trip, provider caching + store-delete, and 6 PlayerViewModel mix tests: duck values, pause lift, stop, slider persist, non-premium lockout, deactivate) |
| TFM builds | Android 0 errors · iOS sim 0 errors |
| Android emulator (self-contained apk, Debug premium unlock) | Real story played with the bed: **both players simultaneously `state:started` in `dumpsys audio`** — narration AudioTrack (mono 24 kHz, ExoPlayer/MediaElement) + ambient `android.media.MediaPlayer` (stereo 24 kHz, CONTENT_TYPE_MUSIC); `story-6-ambient.wav` (6.1 MB) created beside the narration in `files/media/`; slider drag persisted (`tw.music-volume` 0.51); 0 FATAL/crash lines in logcat |
| Screenshot | [`06f-player-music-live.png`](qa-shots/android/06f-player-music-live.png) — narration playing 1:21/2:29 (pause glyph), Voice full, **Music slider mid-position**, Hearth hairline design intact |
| Non-premium path | Music row keeps the dimmed `PREMIUM` tag → paygate route (unit-tested; UI unchanged from prior pass) |

Honest residuals (also in known-gaps): iOS mix is code-parity but not ear-verified (sim QA is screenshot-only); bed render cost on phone silicon unmeasured (render never blocks narration — the bed fades in when ready).

## Weave sheet pass — 2026-07-30 (Generating page → slide-up sheet + mini-bar, founder-directed)

Baseline: 8690fcc + this pass. The Weaving page is deleted; generation now runs behind the whole app in an iOS-style slide-up sheet (`WeaveSheetHost` overlay on Home/Library/Shelf/Settings, one singleton `WeaveSheetViewModel`), with a docked mini-bar when collapsed and a woven-thread progress bar (SkiaSharp: hairline track, stage ticks at the pipeline's 5/45/50/100 boundaries, amber fill with radial glow head, ~2.8 s low-contrast shimmer on the filled portion only) replacing the giant percentage. Recorded as a deliberate reinterpretation in `mockup-spec.md`.

### Real-AI weave evidence (Android emulator, models on device, cooldown cleared via QA lever)

Profile Mila · Willow · 5 min · series; Qwen3-1.7B + Kokoro fp32 already installed. Weave tapped 19:47, done ~19:58 (**"The Moonlit Fox Tale", Episode 6, real recap line from Episode 5, 5:53 narration** — note: much closer to the 5-min target than the earlier 2:45 calibration row). The entire run happened with the sheet — expand, collapse, page-hopping, done, teaser — no page navigation to a Weaving screen at any point.

| Check | Result |
|---|---|
| Sheet opens EXPANDED on "Weave tonight's story" | Verified — slides up over Home, scrim dims the page, no navigation |
| Expanded sheet mid-weave | [`05d-weave-sheet.png`](qa-shots/android/05d-weave-sheet.png) — `WEAVING · 35%`, glow-head fill between the 5% and 45% ticks, Outline done (Success) / Scenes weaving (amber) / rest waiting (Dim), cooldown line, "Close this — we'll keep weaving" |
| Collapse → mini-bar; app fully usable behind it | [`05e-weave-minibar.png`](qa-shots/android/05e-weave-minibar.png) — carousel swiped mid-weave with the bar docked: ember dot, "Weaving Mila's story…", "Outline · 5%", 2px amber progress line on the top edge |
| Cross-page persistence | [`05f-weave-minibar-settings.png`](qa-shots/android/05f-weave-minibar-settings.png) — Parent Settings open (dev-bypass badge) with the same bar showing "Scenes · 12%" |
| Live progress in the bar | Observed 5% → 12% → 27% → 35% → 81% → 90% across pages/expansions with no re-subscription glitches |
| Done state, mini-bar | [`05h-weave-done-minibar.png`](qa-shots/android/05h-weave-done-minibar.png) — Success-tinted "Story ready — tap for the teaser ▶", Success dot, full Success progress line; Home carousel had ALREADY self-refreshed ("The Moonlit Fox Tale · waiting for you" first card) and the hero CTA flipped to "The loom rests — next weave at 20:13"; story-ready notification fired (status-bar icon) |
| Done state, expanded sheet | [`05g-weave-done-sheet.png`](qa-shots/android/05g-weave-done-sheet.png) — `STORY READY`, "Mila's story is ready.", approval sub-copy, all four stages Success, full amber bar, CTA + "↓ close" |
| CTA → Player | Tapping "Story ready — play the teaser ▶" opened the Player modal exactly as the page did (teaser mode, approval CTA, real 5:53 audio); closing it returned to Home with the sheet dismissed and gone |
| Crash scan | 0 FATAL / AndroidRuntime exceptions in logcat across the whole run |

### iOS simulator (screenshot-only — TCC; QA levers)

- [`05d-weave-sheet-ios.png`](qa-shots/ios/05d-weave-sheet-ios.png) — `weaving` QA route now opens Home with the sheet expanded: iOS reinterpretation copy ("Come back anytime — we'll pick up right where we left off."), 30-min cooldown line (no premium override on the sim), rounded-top sheet + handle.
- [`05e-weave-minibar-ios.png`](qa-shots/ios/05e-weave-minibar-ios.png) — new `weaving-collapsed` QA route docks the mini-bar; safe-area fix verified (bar grows by the bottom inset — first capture had it half under the home indicator, fixed in `WeaveSheetHost.ApplySafeAreaPadding`).
- Both TFMs build 0 errors; iOS interaction (drag, tap-expand, real weave) is code-parity, not tap-verified (TCC).

### Kid Shelf safety

The sheet's content is stages + cooldown only (no settings surface). The done CTA ("play the teaser") is additionally HIDDEN while kid mode is active (`ShowTeaserCta` = done AND not kid mode) because the teaser flow carries the parental approval step — covered by unit test `KidMode_HidesTeaserCta_SheetStaysStagesAndCooldownOnly` (live shelf tap-through not exercised this pass; the weave had completed and been dismissed by then).

### Honest Hearth verdict per shot

- `05d` / `05g` (expanded sheet): PASS — tokens exact (BgPhone sheet, Hairline stroke, amber/Success states per spec), typography unchanged from the old page, the progress bar reads as a quiet thread with a warm head; shimmer is invisible in stills by design (low contrast) — verified live only as a gentle drift. One nit: the ScrollView scrollbar flashes on the right edge of the sheet in two captures (system chrome, not layout).
- `05e` / `05f` / `05h` (mini-bar): PASS — SurfaceRaised bar with hairline-weight progress line reads calm and unmistakably "busy"; Success tint on done is quiet, not celebratory. Nit: the bar is flush-square (no top radius) — deliberate, it's a dock, not a card.
- `05d-ios` / `05e-ios`: PASS with the safe-area fix; glyph check clean (▶ carries U+FE0E in the ready copy).
- Motion: all transitions ≤300 ms CubicIn/Out, nothing bounces; ember pulse and shimmer gate off when collapsed/hidden, page not visible, or app backgrounded (window Stopped/Resumed → `WeaveSheetController`).

### Tests

226/226 green (was 217; removed the 4 old WeavingViewModel tests, added 13: fresh-state expand, stage/mini-detail tracking, collapse-persists-across-navigation semantics, mini-bar swipe-down inert mid-weave, done-while-collapsed ready bar + expand + dismiss, teaser-dismisses-sheet, kid-mode CTA hiding, auto-surface on resume progress, failure dismiss, iOS copy, QA expand, stage-tick boundaries, Home carousel refresh on completion).

## Haptics expansion pass — 2026-07-30 ("Add much more haptics")

Founder feedback verbatim: *"Add much more haptics."* The five-word map above grew into a seven-word vocabulary wired as a continuous texture — every meaningful touch answers back, bedtime-quiet, never on passive events.

### Vocabulary (IHaptics, additive)

| Word | iOS | Android | Meaning |
|---|---|---|---|
| `Tick()` **new** | soft impact, intensity 0.4 | `EFFECT_TICK` | lightest possible tap for continuous interactions; **coalesced app-wide to ≥60 ms** (`HapticTickLimiter` in Core, fake-clock unit-tested) so scrubbing can't buzz |
| `Celebrate()` **new** | light impact → medium impact (0.8) ~120 ms apart | two-pulse soft waveform (amplitude-controlled where supported) — never `EFFECT_DOUBLE_CLICK` | gentle two-beat completion |
| existing five | unchanged | unchanged | Light / Selection / Medium / Success / Warning |

All calls remain fire-and-forget, main-thread-safe, swallow-on-error. Debug builds log every fired call as `TWHaptics: <name>` (logcat / sim console) — the emulator-verifiable proxy for feel.

### Full map (site → semantic)

- **Home carousel** — Tick when the snapped card changes mid-swipe (`Scrolled` → nearest index from `HorizontalOffset`/236dp; primed on first event, ±1-step changes only so item refreshes stay silent; <3 cards = no derivable snap = silent). Long-press on a card → Medium as the context menu opens.
- **Weave sheet / mini-bar** — Light on expand AND collapse/dismiss commits; Selection once per gesture when the grab-zone drag crosses the 110dp collapse threshold (arming); **Tick per stage transition** (Outline→Scenes→Poster→Narration — the phone quietly "loom-clicks" through the pipeline even while collapsed; fired from the VM on stage-row change, foreground-gated via `WeaveSheetViewModel.IsForegrounded`, mirrored from the existing `WeaveSheetController` gate by every loaded host); **Celebrate replaces Success** on weave-complete story-ready (foreground-only, once).
- **Player** — Light on modal open and play/pause/stop/replay; seek bar is now scrubbable (new pan in code-behind): Tick per 5% step under the finger (thumb follows live), Selection on seek commit (tap or release); Voice/Music mix sliders Tick at every 25% step and on arriving at either end while dragging; drag-handle moved out of the ScrollView (Android swallows vertical pans inside scroll containers — same lesson as the weave sheet's GrabZone), Tick when the dismiss drag crosses 70dp (arming), release past it closes; Celebrate on "Sounds lovely" approval unlock (was Success).
- **Shelf hold-to-exit** — Light at touch-down (grip), escalating Ticks at 0.5 / 1.0 / 1.5 s during the 2 s hold, Success on unlock (kept), **Warning on early release** ("not yet").
- **Length slider** — detent-crossing Selection kept; added Tick on drag start (grip) and Light on drag commit.
- **Onboarding** — Continue steps keep Light; the final "Start my 7 free nights" is now **Medium** (crossing the threshold into the app).
- **Settings** — toggles keep Selection; Light when the biometric prompt appears; gate pass stays Light; gate fail stays Warning.
- **Premium** — Light on page open; Debug testing-unlock flip → Celebrate (relock stays Selection); premium-gate bumps stay Warning.
- **Deliberately silent** — passive list scrolling, page Appearing, keyboard typing, per-percent progress, and anything while backgrounded.

### Verification

- **Tests: 244/244 green** (was 226; +18: `HapticTickLimiter` coalescing with a fake clock ×4, weave-sheet stage-tick/Celebrate/foreground-gate/expand-collapse-dismiss ×6, player open/seek/stop-replay/approve ×4, premium unlock/open ×2, onboarding final-Medium ×1, settings gate prompt/pass/fail ×1). New recording `FakeHaptics` in `Fakes.cs`.
- **Builds:** Android + iOS simulator both 0 errors.
- **Android emulator functional pass (fresh install, real Qwen3+Kokoro weave):** full flow onboarding → carousel → weave → stage ticks → ready → player scrub → shelf hold, verified via `TWHaptics` logcat lines because haptics can't be felt in emulation. Evidence highlights: onboarding taps `Selection ×2 → Light`; length-slider drag `Tick(grip) → Selection(detent) → Selection+Light(commit)`; final CTA `Medium`; weave `Medium(start) → Light(collapse) → Tick ×3 (stage transitions at 20:42:49 / 20:54:44 / 20:54:50, all while collapsed) → Celebrate (story ready, 21:00:15)`; teaser CTA + player open `Light ×2`; seek scrub `14 Ticks spaced ~83 ms (limiter holding) + Selection commit`; voice slider `5 Ticks (quarter steps + low end)`; approve `Celebrate`; handle drag `Tick(arm) + single close`; carousel swipes `Tick per snap change` (verified with 4 cards; 2-card range <½ cell → correctly silent); shelf early release `Light → Tick → Warning`, full hold `Light → Tick ×3 → Success`; settings `Light(nav) → Light(prompt) → Light(pass)`, toggle `Selection`; premium `Light ×2(open)`, unlock `Celebrate`, relock `Selection`; long-press menu `Medium`. **0 FATAL / AndroidRuntime exceptions across the whole run; no vibrator errors.**
- **Found & fixed during the pass:** (1) Player drag-handle pans were dead on Android — the page ScrollView swallowed them; handle now lives outside the ScrollView. (2) Naive `HorizontalOffset` snap math was primed wrong for item refreshes — priming + single-step guards added; offset confirmed DIP-based on Android by measurement (~200 px shift logs as ~77).
- Residual: haptic *feel* (intensities, the Celebrate two-beat spacing) is subjective and needs the founder's iPhone — the deploy this pass feeds.

---

# Content filters QA — 2026-07-30 (founder bug report: "content filters are not working")

Scope: the Settings "Content filters — Gentle ›" static stub became a live feature — `ContentFiltersPage` (route `content-filters`, inside the biometric-gated flow), `ContentFilterLevel` (Gentle / Brave-ish) persisted as `tw.content-filter`, threaded via the profile seam (`StoryPipeline` stamps `ChildProfile.ContentFilter` from the settings store; both engines read `EffectiveContentFilter`). Brave-ish widens ONLY the allowed-theme palette (LLM system-prompt block for bands 5–6 / 7–8 + three template skeletons); ages 3–4 are always Gentle-equivalent; SafetyFilter and repair loop untouched by level.

| Check | Result |
|---|---|
| Tests | **268/268 green** (was 244; +24: prompt widening/hard-exclusions-at-both-levels/3–4-identity ×9, template gating + Brave-ish-safety-clean ×4, SafetyFilter level-invariance ×1, ContentFiltersViewModel + Settings row ×8, pipeline threading + Brave-ish weave safety ×2) |
| Builds | Android + iOS simulator both 0 errors |
| Settings row live | Android emulator: row shows current level and opens the picker; screenshot [`qa-shots/android/08d-content-filters.png`](qa-shots/android/08d-content-filters.png) (Brave-ish selected, amber radio, whisper footer) |
| Persistence | Selected Brave-ish → force-stop → cold relaunch → Settings row reads "Brave-ish ›"; `shared_prefs` carries `<string name="tw.content-filter">Braveish</string>` |
| Brave-ish weave, no crash | Real Qwen3+Kokoro weave at Brave-ish on the emulator completed all four stages (outline → scenes·polish → poster → narration), story "The Fox Who Wove the Moon: A New Adventure" (5 038 chars, 5:05 narration) reached AwaitingApproval; process alive throughout, 0 FATAL/AndroidRuntime errors (~26 min wall on emulator — consistent with known emulator LLM speed, not a regression) |
| Not covered | iOS on-device tap-through (sim tap automation TCC-blocked — page reachable only via Settings, which needs the gate; code path identical to Android). Brave-ish only affects NEW weaves — existing shelf stories are untouched by design |

---

# Recipe shelf + persistent selections — 2026-07-30 (founder: "I want my selection to be remembered… edit the categories from the generation page — slide-ups, shelves, subtle buttons")

Scope: (1) every Home selection now WRITE-THROUGH persists to the active profile the moment it commits — no Save button; (2) the Home summary line became the entry point to a compact "Tonight's recipe" bottom shelf (interests · narrator · series mode, all live-persisting) in the weave sheet's visual language, via a new reusable `BottomShelfHost` control + `RecipeShelfViewModel`.

### Persistence root cause (the founder's "slider isn't remembered")

`HomeViewModel.SelectLength` only wrote the local `SelectedLength` property; the profile was saved at WEAVE time only, and `ActivateAsync` re-read the stored profile on every appearing — so any pick not followed by a weave silently reverted on the next visit or cold start. Fix: the length command is now async write-through (`SaveProfileAsync` on every accepted commit, summary line recomputed); the premium-gated 15 still never persists. The weave now receives a profile SNAPSHOT so recipe edits during a weave affect only the next story.

### Verification

| Check | Result |
|---|---|
| Tests | **283/283 green** (was 268; +15: Home write-through/cold-restart/rejected-15/summary-rerender/adjust-open/adjust-blocked-while-expanded/next-weave-snapshot ×6, RecipeShelfViewModel open-load/interest-toggle-roundtrip/voice-write-through/no-rewrite-on-reselect/sample-selects/mode-write-through/weaving-whisper/close-haptic/ProfileChanged ×9) |
| Builds | Android + iOS simulator both 0 errors |
| Emulator: length persistence | tap 10 on the track → summary re-renders "· 10 min" instantly → **force-stop → cold relaunch → thumb on 10, summary "10 min"** ([`04g-summary-adjust.png`](qa-shots/android/04g-summary-adjust.png) shows the whispered "adjust ›" affordance) |
| Emulator: shelf persistence | open shelf → toggle Forest ✓ / Ocean off, pick Sage, switch One-shots (each mutation logged `TWHaptics: Selection`) → **force-stop → cold relaunch → summary "Foxes · Magic · Space · Sage · 10 min", shelf reloads every edit** ([`04f-recipe-shelf.png`](qa-shots/android/04f-recipe-shelf.png)) |
| Emulator: coexistence | weave started → sheet collapsed → mini-bar docked ("Weaving Little One's story… · Outline · 5%") → summary tap slides the shelf ABOVE the bar, scrim dims it inert, eyebrow whispers **"for the next story"** ([`04h-recipe-shelf-during-weave.png`](qa-shots/android/04h-recipe-shelf-during-weave.png)); dismiss puts the bar back exactly where it was; weave resume after reinstall re-docked the bar and the shelf still behaved |
| Emulator: dismissal | scrim tap, handle tap and drag-down all close (Light haptic via the VM's single close path); 0 FATAL/AndroidRuntime errors across the whole pass |
| iOS sim | build 0 errors; QA-seeded cold start with `SIMCTL_CHILD_TALEWEAVER_QA_ROUTE=recipe-shelf` opens Home with the shelf up ([`04f-recipe-shelf-ios.png`](qa-shots/ios/04f-recipe-shelf-ios.png)); glyph check clean (✓ / ▶︎ outline / ● / em-dash), safe-area bottom padding applied |

### Honest Hearth verdict

- **PASS** — the shelf reads as the weave sheet's sibling: same BgPhone panel, Hairline stroke, 20-radius top corners, drag handle, ≤300 ms eased motion, nothing bounces. Chips are quiet (hairline pill idle, amber stroke + ✓ selected — the onboarding row language compacted), narrator rows are the onboarding rows verbatim at 52dp, the underline mode pair matches step 2. The "adjust ›" tail is genuinely whispered (Dim 12px) — easy to miss until you want it, which is the point.
- Found & fixed during the pass: **the app's implicit `BoxView` hairline style (`HeightRequest=1`) collapses any code-created scrim to an untappable 1dp line** — the shelf's scrim is a `Grid` instead. The weave sheet's own XAML scrim has the same latent flaw (its near-full-height sheet hides it: only the 64dp header strip goes undimmed and untappable) — recorded in known-gaps, not touched here per ownership.
- Nits: on iOS the sheet runs taller (type metrics) and sits closer to the header than on Android — still clear of it with the scrim visible; the shelf caps at 700dp and scrolls on small phones. Sample-ring playback exercised for no-crash only (audio quality is the onboarding path, unchanged).

---

# Content-aware narration pacing — 2026-07-30 (founder: "sounds like she's reading one line at a time… we may need a varying pause… and measure it")

Scope: the constant-per-class pause table (`ProsodyPlanner`) became a content-aware **`ProsodyDirector`** — every inter-sentence gap is computed from narrative context, deterministically, and the whole plan is measurable (`harness tts --report`).

### The pause model

Base per punctuation class (means deliberately below the old constants — context now adds the drama the flat values tried to bake in): sentence **0.55 s** (was 0.68), emphatic ! ? **0.70** (was 0.78), ellipsis **0.85** (was 0.95), **em-dash 0.62 (new class** — the splitter now soft-terminates at "— Capital", a held breath**)**, paragraph **1.15** (was 1.25). Multiplicative context modifiers, tagged per gap: next starts with a connective (And/Then/So/But/Now/Still/Soon/Next/Later) **−40%**; adjacent quoted dialogue lines **−30%** (conversation flows); next is a dramatic opener (Suddenly / Just then / All at once / All of a sudden / But then / At that very moment) **+30% beat before it**, suppressing the connective rule; previous-sentence length modulation **±15%** (short punchy → shorter after, long → longer breath); deterministic per-gap jitter **±12%** (FNV-1a over the previous sentence × gap index — platform-stable, no `string.GetHashCode`). Clamps: sentence-family gaps **[0.30, 0.95] s**, paragraph **[0.85, 1.50] s**. Anti-monotony is explicit and two-fold: adjacent gaps are nudged ≥25 ms apart, and a third consecutive near-identical gap (±40 ms) is pushed clear — the metronome cannot re-emerge even by slow drift. The LLM writes the beats *via punctuation*: the polish prompt now instructs ellipsis = dreamy trail-off, em-dash = tiny held breath, short sentences = excitement, paragraph breaks = resting points; the text stays the single source of truth. Template fillers got light matching seasoning (two sentence-final ellipses, two inline em-dashes, an ellipsis before the final "Goodnight.").

### Measured — same texts, old constants vs new director

| Text | Model | Gaps | Mean | CV | Min–Max | Longest near-identical run (±40 ms) |
|---|---|---|---|---|---|---|
| Fox/pier audition (2 ¶, dialogue, ellipsis, em-dash) | old constants | 12 | 0.75 s | **0.22** | 0.68–1.25 | **6** |
| same | new director | 13 | 0.56 s | **0.45** | 0.30–1.22 | **2** |
| Real Qwen3 weave (818 words, seed 20260730, age 6, foxes/ocean) | old constants | 60 | 0.83 s | **0.30** | 0.68–1.25 | **5** |
| same | new director | 60 | 0.72 s | **0.42** | 0.35–1.47 | **2** |

Real-story rule shares: len 95% · jit 100% · mono 13% · dlg 7% · conn 3% · clamp 2% (drama 0% — this Qwen3 story used no dramatic openers; the channel is prompt-fed and template-exercised). Report excerpt (real weave):

```
[ 3] para(1.15) len(+15%) jit(+4%) → 1.38s        → "The fox jumped into the water…"
[ 7] para(1.15) len(+6%) jit(-9%) → 1.11s         → ""Will this help you?""
[ 8] emph(0.70) len(-14%) jit(-9%) → 0.55s        → "Riley asked."
[21] sent(0.55) dlg(-30%) len(+4%) jit(-6%) → 0.38s → ""The light is here, just like…""
[27] sent(0.55) conn(-40%) len(+2%) jit(+12%) → 0.38s → "And so, she fell asleep…"
[45] para(1.15) conn(-40%) len(+10%) jit(+9%) clamp(0.85) → 0.85s → "And so, she fell asleep…"
[48] sent(0.55) dlg(-30%) jit(+9%) → 0.42s        → ""It's from the sea.""
[55] sent(0.55) dlg(-30%) len(-12%) jit(+2%) → 0.35s → ""You're kind.""
[41] sent(0.55) len(+6%) jit(+4%) mono(-0.030) → 0.58s → "The fox looked at Riley with gratitude"
[13] sent(0.55) len(-10%) jit(+4%) mono(-0.029) → 0.49s → "The stars above twinkled like tiny lan"
```

### Verification

| Check | Result |
|---|---|
| Tests | **326/326 green** (was 285; prosody suite rewritten + extended: gap-class detection, connective/dialogue/drama/length/jitter rules, em-dash splitter rules, full-plan determinism, clamp coverage, anti-monotony on pathological identical-sentence input, and acceptance thresholds pinned on a fixed 14-sentence fixture — CV ≥ 0.25, longest near-identical run ≤ 2; ProsodyReport metric math unit-tested) |
| Builds | Android 0 errors · iOS simulator 0 errors (pure Core + prompt change; no emulator pass needed) |
| Timestamps | `NarrationManifest` accumulation verified through the varied gaps on the real render — e.g. sentence 7 starts at 22.78 s = 21.83 (prev end) + 0.95 (planned gap), sentence 10 at 32.42 s = 31.20 + 1.22; the engine consumes the identical plan the report prints (pure function of the script), and the timestamp mirror test now runs against a planned gap instead of a constant |
| Audition file | [`qa-audio/paced-ember.wav`](qa-audio/paced-ember.wav) regenerated with the director (41.9 s, Ember, fp32 Kokoro): fox-on-the-pier two-paragraph text with a dialogue exchange, a dreamy ellipsis into a *Suddenly* beat (0.45 → 0.95 s), an em-dash held breath, and a paragraph beat into "But then…" (1.22 s) |
| Determinism | Same input → same gaps asserted by test; jitter/nudges are integer-hash based (platform-stable), tags culture-invariant |

### Honest verdict

The metronome is measurably gone: on the same real story the old model produced 43 *identical* 0.68 s gaps (longest sameness run 5); the director's 60 gaps span 0.35–1.47 s with no three alike and dialogue that actually flows (0.35–0.42 s between quoted lines). Ear-check on the Mac matches the numbers — the *Suddenly* beat and the paragraph breaths land, dialogue no longer waits. Honest limits: drama fired 0% on this particular Qwen3 story (prompt channel is new; templates and fixture exercise it), `mono` corrections are audible only as *absence* of sameness, and the new pacing has not yet been founder-auditioned or heard on phone hardware — `paced-ember.wav` is the audition artifact for exactly that.

---

# Poster feedback pass — 2026-07-31 (founder: line artifact · dead parallax · "we can do better" posters)

Scope: three founder mandates on the poster stack. (1) Kill the "arbitrary line with a dot on its end lying across the crescent moon" seen top-left on the posters. (2) Make the player poster's 4-layer parallax actually work — it had ZERO effect on device, and the promised sensorless idle drift did not exist. (3) Raise poster craft a level: multi-candidate formation (two LLM samples + template safety) judged by a new deterministic `CompositionCritic`, plus crescent inner shadow, corner vignette, and sky-gradient dither.

### Mandate 1 — the line artifact: root cause + fix

The artifact was the **ShootingStar painter**: a 3.2×scale-long, hard-edged gradient stroke with a heavy head dot. The LLM includes a ShootingStar in most formations (it was listed neutrally in the poster prompt), and the composer clamped its Y into 0.06–0.30 — exactly the moon band — with **no X clearance and no frequency control**, so the streak routinely lay across the moon and clipped the top edge into a hard rod. Verified against the emulator's real stored specs: stories 2 and 5 both carried `ShootingStar` at (0.40, 0.06) / (0.20, 0.25) overlapping their moons at (0.30, 0.15) / (0.14, 0.12) — the founder's screenshot exactly.

Fix, three layers deep (all deterministic and idempotent, pinned by tests):
- **Painter** (`SkiaPosterRenderer.DrawShootingStar`): tail shortened 3.2×→2.1×scale, stroke slimmed (0.045×, blurred edge), alpha fades from 85% through 30% to zero at the tail, head is a small soft-haloed spark instead of a hard dot.
- **Composer** (`PosterComposer`, ArtVersion 7→**8**): `ShootingStarAllowed(seed)` admits a shooting star on ~1 poster in 4 (seed-hashed); `PlaceShootingStar` clamps Y to 0.12–0.28 (never top-clipped), scale to ≤0.15, keeps the head ≥0.26 from the moon and mirrors the tail to sweep AWAY from the disc, whole streak on-canvas. Because the gate runs in `Validate`, **already-stored specs are cleaned on re-render** — the v8 art refresh removed the artifact from every existing shelf story without touching the DB.
- **Prompt** (`LlamaPrompts` POSTER section only): ShootingStar is now described as "one rare wish-streak — include ONLY when the story itself is about stars or wishes, most posters have NONE, never near the moon".

### Mandate 2 — parallax: root cause + fix + measured proof

Root cause: `ParallaxPosterView` started its motion (sensor OR drift) **only from the `Loaded` event — which never fires for views inside Shell modal pages on iOS** (PlayerPage is `ModalAnimated`). Nothing started, so neither tilt nor any fallback ran: exactly the founder's "zero effect". Two secondary gaps: a started-but-silent sensor left the poster frozen with no handover, and nothing stopped motion on app backgrounding. (Instrumentation confirms the diagnosis: with the fix in place, the start signal that actually fires on BOTH platforms is handler attach — `TWParallax: start (HandlerAttached)` — not Loaded.)

Fix (rewritten `ParallaxPosterView`): motion starts on `Loaded` **and** `OnHandlerChanged` (idempotent — first signal wins), stops on Unloaded/handler detach, and pauses/resumes with `Window.Stopped/Resumed`; accelerometer gravity is low-passed at α=0.08 into per-depth translations clamped to [4, 9, 15, 24]px; a 2 s watchdog hands a silent sensor over to the drift loop; and the **idle drift now exists for real** — a slow figure-eight (~16 s cycle) on a dispatcher timer (yields by construction, per the animation-loop gotcha), driving the same low-passed offsets. A fixed vignette overlay rides above the translating layers. Debug builds log every lifecycle edge as `TWParallax:` lines.

| Check | Result |
|---|---|
| Sensor path, Android emulator | logcat: `start (HandlerAttached)` → `accelerometer started` → `rendered 4 layers + vignette at 683x683`; `adb emu sensor set acceleration 6.9:6.9:0` vs `-6.9:6.9:0` (rotation locked) produces visibly shifted layers — [`qa-shots/android/06g-parallax-a.png`](qa-shots/android/06g-parallax-a.png) / [`06g-parallax-b.png`](qa-shots/android/06g-parallax-b.png) |
| **Measured pixel shift** | foreground fox (depth 3, travel 24): **89 px**; sky landmarks (depth 0, travel 4): **15 px** — a 5.9:1 ratio against the design's exact 6:1 travel contract, dy = 0.0 px (pure X tilt) |
| Drift path, iOS simulator (no accelerometer) | console: `accelerometer not supported → drift` → `idle drift started`; two screenshots 5 s apart show the poster region moving (foreground landmark drifted ~23 px; ~62 k px changed inside the poster bbox only) — [`qa-shots/ios/06i-player-drift-a.png`](qa-shots/ios/06i-player-drift-a.png) / [`06i-player-drift-b.png`](qa-shots/ios/06i-player-drift-b.png) |
| Stops when backgrounded / page closes | `Window.Stopped` and handler detach both call the same teardown (sensor unsubscribed + timers stopped); code path exercised by page close during the pass, no sensor-leak errors in logcat |
| Not covered | Real-iPhone tilt feel (the standing physical-deploy step) — the sensor path is the same code the emulator ran |

### Mandate 3 — multi-candidate formation + deterministic judge

- `LlamaStoryEngine.GeneratePosterSpecAsync` now generates **two** seeded formation samples (~350 output tokens each, sequential on the loaded model) plus the template engine's formation as safety; all three are composer-validated and scored by `CompositionCritic` (Core/Poster, no model, fully deterministic); the winner ships. A failed LLM sample just drops out of the running — the poster stage can no longer sink a weave on JSON failure.
- `TemplateStoryEngine` runs the same judge across its three per-theme sub-variants, so the no-models path also picks its strongest composition per outline.
- Real judged example (harness `poster --age 5 --interests foxes,stars --seed 42`, real Qwen3 on the dev Mac; outline "The Star-Spangled Fox", settings bedroom/window/garden/firepit): `llm-1: total 0.510 (balance 0.44, depth 1.00, focal 0.98, relevance 0.50, palette 0.25, crowding −1.00)` · `llm-2: total 0.480 (balance 0.47, depth 1.00, focal 0.85, relevance 0.50, palette 0.25, crowding −1.00)` · `template: total 0.797 (balance 1.00, depth 1.00, focal 0.99, relevance 0.50, palette 1.00, crowding −0.00)` → **template won** — both LLM samples had piled grounded motifs onto the focal (the exact clutter the founder disliked) and picked an off-story palette. Poster stage wall: 84 s on M-series (two samples + judge; scenes dominate the weave regardless).
- Painter craft: crescent gained a soft inner shadow along its bite edge; every flattened/layered composition gains a ~6% corner vignette (fixed, never slides with parallax); sky gradients get ±1-LSB tiled hash dither.

### Verification

| Check | Result |
|---|---|
| Tests | **326/326 green** (+13 this pass: critic determinism/focal/balance/relevance/crowding/depth/palette/judging ×9, shooting-star gate rate/drop/moon-clearance/idempotence ×4). Note: baseline grew in parallel with the concurrent TTS workstream's tests — 326 is the whole suite at the end of this pass |
| Builds | Android (`EmbedAssembliesIntoApk` APK) + iOS simulator both 0 errors, 0 warnings in touched files |
| Art regen on device | ArtVersion 8 → cold launch re-rendered every shelf thumbnail (`story-{1,2,5}-thumb-v8.png` on disk, v7 files deleted) with **no stray line on any poster** — [`qa-shots/android/04i-carousel-v4.png`](qa-shots/android/04i-carousel-v4.png), [`06h-player-v4.png`](qa-shots/android/06h-player-v4.png) |
| Render-dump sweep | 30+ compositions dumped (template themes × seeds, painter galleries at 200/64 px, LLM-style specs, chaos repair, the emulator's real stored specs, and three gate-passing star/space seeds): **zero stray lines**; the shooting star appears only on gate-passing seeds, clear of the moon, as a delicate fading streak |
| Emulator stability | 0 FATAL / AndroidRuntime exceptions across install → art regen → player → sensor sweeps |

### Honest Hearth verdicts (per screenshot)

- [`04i-carousel-v4.png`](qa-shots/android/04i-carousel-v4.png) — **PASS.** Both fox cards regenerated clean; the moonglade, staggered treelines and vignette read storybook; no artifact anywhere. The dither is invisible at card size (its job is banding prevention, not texture).
- [`06h-player-v4.png`](qa-shots/android/06h-player-v4.png) — **PASS.** Poster hero clean at 226 dp; vignette grounds the corners without reading as a frame; layers + fixed vignette confirmed live in logcat.
- [`06g-parallax-a/b.png`](qa-shots/android/06g-parallax-a.png) — **PASS with the numbers above.** The tilt reads as depth, not slide: the fox travels ~6× the sky, and nothing shears or gaps at ±0.7 g tilt (1.15× overscan holds at full travel).
- [`ios/06i-player-drift-a/b.png`](qa-shots/ios/06i-player-drift-a.png) — **PASS.** The drift is a whisper (ken-burns, ~16 s cycle) — alive without ever bouncing; exactly the degraded-path feel the design asks for.
- Dump-loop nit, recorded honestly: at 512 px the dither is faintly perceptible in flat sky if you look for it (amplitude already halved once during the pass); at device density it is not visible in any screenshot. The critic's crowding penalty saturates quickly (both LLM samples hit −1.00) — directionally right for the clutter problem, but its calibration is young; noted in known-gaps.

---

# Parallax — physical-iPhone pass, 2026-07-31 (founder, third report: "still not working")

**This is the first time the parallax was verified on real hardware.** The two previous passes
declared it fixed from an Android emulator with injected sensor values and an iOS simulator
with no sensor at all. Both verdicts were wrong, and the reason they were wrong is the whole
lesson of this pass: *a synthetic sensor does not produce the attitudes a hand produces.*

Device: Dean's iPhone 17 Air (`CAF80486-…`), Debug `ios-arm64`, `TALEWEAVER_QA_ROUTE=player`,
console attached via `devicectl device process launch --console`, every line prefixed
`TWParallax:`. Raw log: 4 deploy/capture cycles, `/private/tmp/tw-parallax-console-{2,3,4}.log`,
plus an in-container copy at `Documents/tw-parallax.log` for locked-phone launches.

### What the previous fix got wrong

The lifecycle diagnosis (motion started only from `Loaded`, which never fires inside a Shell
modal on iOS) was **correct and remains fixed** — the device log confirms it, with
`start (HandlerAttached)` firing 20 ms before `size changed` and `start (Loaded)` arriving
later as a no-op. But it was only half the story, and the half it missed is the half the
founder could see.

The sensor was running and delivering. The **mapping from tilt to pixels** was the bug:

| | Old | Consequence on a hand-held phone |
|---|---|---|
| Neutral reference | none — raw gravity fed straight in | — |
| Gain | 1.0 | — |
| Vertical channel | `translateY = rawY × travel` | MAUI normalizes iOS CoreMotion to the Android convention, so a held phone reads **rawY ≈ +0.85**. The vertical offset therefore sat at 85–100 % of full travel *permanently*. First device capture: `ty=[6,14,24,38]` — the clamp — **unchanged across 1 750 readings** while rawY swung 0.94 → 0.05. Exactly the founder's second report, "just works sideways". |
| Horizontal channel | `translateX = −rawX × travel` | A comfortable roll is ±0.15 g, so the foreground moved ±3.6 px and the sky ±0.6 px on a 226 pt poster. Numerically alive, visually dead. |
| Travel | `[4, 9, 15, 24]` px, 6:1 spread | Even at full deflection the back layer moved 4 px. The founder's third report — "just the FRONT layers get parallax" — was the back layers sitting under the perception threshold, not sitting still. |

Why the emulator missed it: the earlier QA injected `acceleration 6.9:6.9:0` versus
`-6.9:6.9:0` — ±0.7 g on **both** axes at once, an attitude no hand reaches — and measured a
healthy 89 px. The fix looked proven. **Sensor injection at extremes cannot validate a
mapping that fails at hand scale.**

### The fix

1. **Neutral-referenced tilt, both axes.** `tilt = clamp((raw − neutral) × 2.0, −1, 1)` with
   `neutralX = 0.0` (roll is absolutely referenced) and `neutralY = +0.75 g` — a natural
   bedtime reading angle, ~41° back from vertical — so a phone in the hand sits near the
   *centre* of the vertical range rather than against its clamp. Full travel at 0.5 g of
   deviation (~30° of tilt) per axis, independently, so a diagonal tilt gives a diagonal
   offset. Low-pass α = 0.18 at `SensorSpeed.Game` ≈ an 80 ms time constant: it tracks the
   wrist instead of trailing it.
2. **Direction is the window metaphor.** Content moves *opposite* the edge you tip away: drop
   the right edge and the scene slides left; tip the top edge away and the scene slides down,
   revealing sky.
3. **Travel `[4,9,15,24]` → `[6,14,24,38]` → `[12, 20, 30, 40]`.** The 6:1 design spread is
   deliberately compressed to 3.3:1. Depth *ordering* is what sells the illusion; a spread
   that wide simply parks the back layers below the eye's threshold.
4. **The sky gradient is split off depth 0 and pinned.** A smooth vertical ramp looks identical
   translating or still, so holding it fixed costs nothing and buys everything: the depth-0
   stars and moon now travel *against* a stable backdrop instead of with it. The stack is five
   images — fixed sky · 4 travelling layers · fixed vignette.
5. **Bleed is derived, not guessed.** Every layer is rendered at `view + 2 × maxTravel` on both
   axes (226 → 306 pt, 1.354×) and laid out as `Fill` with a **negative margin**, not
   `WidthRequest` + `Center`. MAUI's arrange path clamps a centred child to
   `min(container, desired)`, which silently discards overscan; the Fill path inflates by the
   margin and keeps it. Proven on device, post-layout: `frame=(-40,-40) 306x306`.
6. **Drift is now strictly a sensorless-host fallback** (founder mandate). It starts only when
   the accelerometer is absent or silent for 2 s, is torn down the instant a real reading
   arrives, and `_sensorEverDelivered` locks it out permanently once the sensor has ever
   spoken. Previously nothing stopped the drift timer when a reading arrived, so both loops
   could have run last-writer-wins.

### Device evidence

Startup and geometry — every layer travelling, bleed on both axes:

```
TWParallax: start (HandlerAttached) · supported=True monitoring=False everDelivered=False
TWParallax: accelerometer started · monitoring=True
TWParallax: FIRST reading after 170ms raw=(-0.307,0.019,0.941) coldStart=True
TWParallax: rendered fixedSky=True + 4 travelling layers + vignette · view=226x226
            bleed=40pt layer=306x306pt image=918x918px density=3 travels=[12,20,30,40]
TWParallax: layer[0] travel=12 frame=(-40,-40) 306x306   ← sky stars, bled on BOTH axes
TWParallax: layer[3] travel=40 frame=(-40,-40) 306x306
```

Tilt response, both axes, all four layers, depth-proportional (`[6,14,24,38]` build — the
mapping and arbitration are identical in the final build, only the travel constants changed):

```
r#650  raw=(-0.186,0.855,0.469) filt=(-0.304, 0.209) drift=False tx=[-1.7,-4.1,-6.9,-11]  ty=[-1.2,-2.9,-5,-7.9]
r#700  raw=(-0.272,0.342,0.883) filt=(-0.530,-0.625) drift=False tx=[-3.2,-7.4,-12.6,-20] ty=[3.5,8.2,14,22.2]
r#900  raw=(-0.297,0.295,0.687) filt=(-0.591,-0.776) drift=False tx=[-3.5,-8.3,-14.2,-22.4] ty=[4.5,10.5,17.9,28.4]
r#950  raw=( 0.077,0.273,0.951) filt=( 0.148,-0.958) drift=False tx=[0.9,2.1,3.5,5.6]     ty=[5.7,13.4,23,36.4]
RANGE(5s) rawX=[-0.762..0.484] rawY=[-0.314..1.217] → front swing x=76pt y=76pt · sky swing x=12pt y=12pt
```

`r#900` is the diagonal case: X and Y in opposite quadrants, both channels live, every layer
non-zero, and each row is exactly `filt × travel` — 0.776 × [6,14,24,38] = [4.5,10.5,17.9,28.4].
`RANGE` is a rolling 5 s min/max: a full hand sweep drives the front layer through its entire
76 pt span on **both** axes.

Ambient / phone-at-rest, the founder's "must not animate on its own":

```
RANGE(5s) rawX=[0.065..0.086] rawY=[0.264..0.277] → front swing x=1.6pt y=1pt · sky swing x=0.2pt y=0.2pt
r#1250 ... drift=False tx=[0.9,2.1,3.6,5.7] ty=[5.8,13.4,23,36.4]
r#1300 ... drift=False tx=[0.9,2.1,3.6,5.8] ty=[5.8,13.4,23.1,36.5]
```

Offsets frozen to within a tenth of a point across a still minute, `drift=False` on every line,
and the string `idle drift STARTED` **never appears in any physical-device log**.

Sensorless fallback still intact, iOS simulator (iPhone 17 Pro, iOS 26.1) — the *only* host
where drift may run:

```
TWParallax: start (HandlerAttached) · supported=False monitoring=False everDelivered=False
TWParallax: accelerometer NOT supported → drift fallback
TWParallax: idle drift STARTED (sensor unavailable)
```

### Verification

| Check | Result |
|---|---|
| Tests | **339/339 green** at the close of this pass (326 at its start; the parallax work itself changed one test, `Resolve_ParallaxTravelIsDesignContract`, and the rest of the growth came from the concurrent UI workstream running in the same tree). That test now also pins strictly-increasing depth ordering, a **≥10 pt perceptible floor on the back layer**, and a 2.5–4.0× spread ceiling — the two properties whose absence cost three founder passes |
| Builds | iOS device `ios-arm64` (signed), iOS simulator, Android — all **0 errors** |
| Physical iPhone | Sensor starts, first reading in 71–246 ms, both axes tilt-correlated, all four layers non-zero and depth-proportional, no drift ever |
| Screenshot, near-full vertical deflection | [`qa-shots/ios/06j-parallax-device-flat.png`](qa-shots/ios/06j-parallax-device-flat.png) — poster clean at `ty ≈ +36 pt`; **no edge, gap or background bleed-through**, vignette stationary, corners intact. This is the case the 1.15× overscan would have failed |
| **Screenshot pair, two hand-held tilts** | [`06k-parallax-device-tilt-a.png`](qa-shots/ios/06k-parallax-device-tilt-a.png) at `raw=(-0.178,+0.872) → tx=[-3.4,-5.7,-8.5,-11.4] ty=[-2.7,-4.5,-6.8,-9.0]` versus [`06k-parallax-device-tilt-b.png`](qa-shots/ios/06k-parallax-device-tilt-b.png) at `raw=(-0.215,+0.737) → tx=[-5.8,-9.7,-14.5,-19.4] ty=[-0.5,-0.9,-1.4,-1.8]` (each frame paired to the reading logged within 1 s of its capture). **6.5 % of the pixels inside the poster bbox changed; the static title strip immediately below it is bit-identical, 0 px changed** — the motion is real, and it is confined to the poster. By eye: the tent apex travels ~24 device px while the star field travels ~7 px, the ~3.3:1 the travel table asks for, and the pinned sky gradient's horizon band does not move at all |
| Back layer perceptibly alive | Sustained hand tilting logged `sky swing x=10.6…21.7 pt` across twelve consecutive 5 s windows (front 35–72 pt). The founder's "I expect to see some stars move gently in the back" is now a 10–20 pt star drift, not the old 4 px |
| Pauses when backgrounded | `stop (WindowStopped) · readings=3851` in the device log the moment the app left the foreground — sensor unsubscribed, timers stopped, no leak |

### Lesson recorded

Never again accept emulator sensor injection as proof that a motion mapping works. Injection
validates *plumbing*; only a hand validates *gain*. Three founder passes were spent on a
feature whose plumbing was fine from pass two onward.

---

## Pass 10 — affordances, analytics removal, story propagation (2026-07-31)

Three founder items in one pass. Base: tip `facd0cd`, 326/326 tests green.

### Item 1 — "Buttons are not obvious. Fix with UI magic."

> *"Buttons like back, adjust and so on are very much not obvious — do some UI magic rather
> than the almost unreadable and easy-to-miss buttons."*

The diagnosis was not "Hearth is too quiet"; it was that the implementation had drifted past
quiet into **absent**. Back was 14px `Faint` ghost text with no container. "adjust ›" was a
`Dim` tail on a Caption — the same colour the spec reserves for *disabled numerals*. "All →",
`UPGRADE →` and the Player's `■ stop` / `↻ replay` were bare accent-or-Faint labels with
roughly 16–18dp of vertical hit area. Three separate signals were all saying "decorative".

The fix is a **shared affordance system**, not a set of one-off tweaks — new styles in
`Resources/Styles/Styles.xaml`, applied everywhere:

| Style | Metrics | Used for |
|---|---|---|
| `PillButton` | transparent, 1px `Border` stroke, radius 22, **44dp**, padding 20 | neutral secondary actions |
| `PillButtonAccent` | as above, `Accent` text | the action you want found |
| `PillButtonCompact` / `PillButtonAccentCompact` | radius 20, **40dp**, padding 16, 13px | header chips, transport controls |
| `BackPill` | `‹  Back`, aligned Start | one back, one position, every screen |
| `RowValue` | `Accent` 14px + `›` | a nav row's trailing value |
| `TappableWhisper` | `Accent` 13px OutfitMedium | small tappable text (premium hints) |

Pressed on every one: `SurfaceRaised` fill, `Accent` border, `Accent`/`AccentHover` text.
Haptics come from the bound command (`ShellNavigator` fires Light on every navigation and
coalesces, so nothing double-thumps).

Four rules the pass encodes, and they are now written into the design contract:

1. **`Dim`/`Faint` is never the sole signal that a control is live.** Idle picker rows lifted
   to `Muted`; every tappable whisper is `Accent`.
2. **Every tappable clears ~44×44dp.** `ToggleSwitchView` keeps its 44×26 capsule but the
   control is now 64×44 of transparent touch area; the narrator sample ring 34→40; the Home
   avatar ring 32→40; the Player seek strip 30→44; both drag handles 36/──→44.
3. **Every tappable has a pressed state.** Buttons via VSM; Library rows, Shelf tiles,
   Settings nav rows and the Player's 64dp play disc via CommunityToolkit `TouchBehavior`
   (`PressedBackgroundColor` / `PressedOpacity` / `PressedScale`), commands bound through a
   named page element — behaviors do not inherit BindingContext (standing gotcha).
4. **A chevron means "this navigates."** Settings → Storage is read-only, so it lost its
   chevron. A dead affordance is worse than a quiet one.

Also: the Player gains a `‹  Close` pill beside its drag handle and the recipe shelf a `Done`
pill, because a gesture hint is not an affordance; the Player's Music·PREMIUM row is no longer
dimmed to 55% behind a `Faint` tag (Accent `PREMIUM →` instead); the Kid Shelf lock pill grew
to the shared metrics with `Muted` text. `GhostButton` survives for footer text only and is
marked deprecated as an affordance.

| Screenshot | Verdict |
|---|---|
| [`android/04j-buttons-v2.png`](qa-shots/android/04j-buttons-v2.png) | **PASS.** `☾ Kid Shelf`, `Adjust ›` and `All ›` all read as buttons at a glance and still whisper — hairline capsules, no fill, no gradient. The `Adjust` pill sitting right of the summary line is the single biggest legibility win on the screen. Avatar ring at 40dp now looks intentional rather than incidental |
| [`android/09b-library-buttons.png`](qa-shots/android/09b-library-buttons.png) | **PASS.** `‹ Back` top-left, unmissable; row chevrons are Accent rather than Dim. Honest caveat: the back pill pushes the eyebrow/headline down ~90dp, which costs one visible row on a 1080×2400 screen. Judged worth it |
| [`android/08e-settings-no-analytics.png`](qa-shots/android/08e-settings-no-analytics.png) | **PASS.** Back pill top-left; `UPGRADE →` is a pill inside the amber trial card; `Little One ›` / `Brave-ish ›` are Accent values; **Storage shows `64 MB, on device` with no chevron**; the Quality analytics row is gone |
| [`ios/04j-buttons-v2-ios.png`](qa-shots/ios/04j-buttons-v2-ios.png) | **PASS, with one caveat.** Pill geometry, stroke weight and Accent text render identically to Android, and the `☾` and `›` glyphs stay in text presentation (no colour-emoji regression). Caveat: iOS is screenshot-only here (sim tap automation is TCC-blocked), so the **pressed states on iOS are code-parity, not eye-verified** — recorded in known-gaps |

Content filters was opened via the new `TouchBehavior` nav row and returned via the new back
pill on the emulator, so the row-level command binding through `x:Reference` is proven live,
not just compiled.

### Item 2 — Quality analytics removed entirely

Gone, in this order: the Settings toggle row, `ISettingsStore.QualityAnalytics`,
`InMemorySettingsStore` + `PreferencesSettingsStore` implementations, the `tw.quality-analytics`
Preferences key, `SettingsViewModel`'s property / partial handler / `ToggleAnalyticsCommand`,
and the toggle assertion in `ViewModelTests`. There was never a sink, an emitter or an
endpoint to delete — the feature was a switch wired to a bool — so the removal is total.

Two things make it stay removed:

- `PreferencesSettingsStore`'s constructor deletes the orphaned key. **Verified on the
  emulator:** `shared_prefs` carried `tw.quality-analytics = true` from a previous session
  before this build; after one launch the key is absent. An existing install cannot keep a
  stale opt-in on disk.
- A reflection test asserts that neither `ISettingsStore` nor `SettingsViewModel` exposes any
  member containing "Analytic". Re-adding it silently now fails the suite.

`docs/prd.md` §6 and `docs/architecture/overview.md` §10 updated. **One honest loose end:**
the project `CLAUDE.md` still carries "Analytics is local-only and OFF by default … events go
to a local sink with PII masked" as an invariant. That line is now false. `CLAUDE.md` was
outside this pass's ownership, so it is flagged in `docs/known-gaps.md` for a founder-owned
edit rather than changed here.

### Item 3 — Newly-generated stories appear everywhere they should

**Root cause.** Only Home subscribed to anything, and only to `IWeaveHost.WeaveCompleted`.
`LibraryViewModel` and `ShelfViewModel` loaded their lists exclusively in their page's
`ActivateAsync`, called from `OnAppearing`. So *every* story change that happened while one of
those pages was on screen — a weave finishing, a teaser being approved, a favourite, a delete,
a cancelled weave discarding its half-woven row — left that page stale until the user
navigated away and back. Home was fine for completions and stale for everything else
(approval, favourite from another surface). It was never a filtering bug: the three lists'
predicates were already right.

**The fix is one seam, placed at the choke point.** `IStoryCatalog` (Core/Abstractions) is
raised by `IStoryStore` **itself** on every `SaveStoryAsync` and `DeleteStoryAsync` — not by
each producer. That is deliberate: bolting an event onto the pipeline, the player and each
view-model would have left the next writer to remember. Putting it in the store makes the
guarantee structural — there is no way to change a story without every list hearing about it.

Supporting pieces, each solving a real problem rather than decorating the design:

- `StoryCatalog.Dispatcher` — Core cannot reference MAUI, and the pipeline writes stories from
  a background task while subscribers mutate `ObservableCollection`s bound to live
  `CollectionView`s. `MauiProgram` injects `MainThread.BeginInvokeOnMainThread`; tests and the
  harness leave it null and get synchronous raises they can await.
- `CoalescingReloader` — a single weave writes its story row **six times** (title, prose,
  poster, teaser, completion, plus the initial insert), and three lists are subscribed. Without
  coalescing each list would stack six overlapping sqlite reads that could land out of order.
  One in-flight pass plus at most one queued follow-up.
- `StoryListItem.SyncInto` — reconciles in place, keyed on story id, instead of `Clear()` +
  re-add. A full rebuild would reset the Home carousel's scroll offset six times per weave and
  de-prime its snap-tick heuristic each time.

**The visibility rules, now decided and documented** (`docs/design/mockup-spec.md`):

| Surface | Weaving | AwaitingApproval | Ready | Failed |
|---|---|---|---|---|
| Home carousel | no | **yes** ("waiting for you") | yes | no |
| Library | **yes** ("weaving…", tap opens the sheet) | yes | yes | **yes** ("didn't finish") |
| Kid Shelf | no | **no** | yes | no |

The Kid Shelf exception is the one worth defending: an AwaitingApproval story has not been
heard by a grown-up, and the shelf is the single surface a child drives unsupervised. Showing
it there would route around the approval covenant. It lands the instant the teaser is approved
— and now it lands *live*, without leaving and re-entering the shelf.

**Coverage.** 11 new tests in `StoryPropagationTests` + `CoalescingReloaderTests`. They hold
all three list view-models alive **at once** and never re-activate them, which is exactly the
situation that used to go stale: a Ready story appearing on all three; AwaitingApproval on two
with the right meta and *not* on the shelf; approval through the real `PlayerViewModel` path
unlocking the shelf tile; a weaving row that is Library-only and then becomes visible
everywhere on completion; Failed staying Library-only; delete clearing all three; a cancelled
weave discarding its row; favourite propagating; newest-first + the ≤8 cap; and row identity
surviving a reload.

### Verification

| Check | Result |
|---|---|
| Tests | **340/340 green** (326 at the start of the pass; −1 the deleted analytics toggle assertion, +15 new: the analytics reflection guard, 11 propagation tests, 2 reloader tests, and the carousel-head signal test) |
| Builds | Android (`EmbedAssembliesIntoApk`, apk) and iOS simulator (`iossimulator-arm64`) — both **0 errors** |
| Stale analytics key | Present as `true` before this build, **absent after one launch** — constructor cleanup verified on device, not asserted from source |
| Live propagation, Library | **PASS, and the strongest single piece of evidence in this pass.** A real Qwen3 + Kokoro weave was started from Home (10-minute target, ~33 min on the emulator), the sheet collapsed, and the Library was opened and **never left**. On that one page, in order: a `weaving… · story` row with no title and no art appeared at the top; its **title** filled in when the outline stage wrote it; its **poster thumbnail** appeared when the poster stage rasterized it; and at 100 % the meta flipped to `waiting for you · 11 min` while the mini-bar turned Success-green. Four separate store writes, four live updates, zero navigation. The pre-fix build could produce none of them |
| Approval → Kid Shelf | **PASS.** Approving the teaser in the player flipped the Library row from `waiting for you · 11 min` to `One-shot · 11 min`, and the story is on the Kid Shelf ([`04m-shelf-propagation.png`](qa-shots/android/04m-shelf-propagation.png)) — the Ready-only rule holds in both directions |
| Library row tap (`TouchBehavior` command) | **PASS.** Tapping the top row opened the correct story in the player, so the `x:Reference`-bound `CommandParameter` resolves — the standing "behaviors don't inherit BindingContext" trap is handled, not tripped |
| Kid Shelf lock pill at the new metrics | **PASS.** The 2-second hold still exits to Home at 44dp/`Muted` |
| **Bug found and fixed during this QA** | The new story reached the Home carousel's collection but **was not the visible card**. `SyncInto` inserts at index 0 and Android's RecyclerView anchors on the item it was already showing, so tonight's story sat off-screen to the left while yesterday's stayed under the eye — precisely the symptom the founder reported, surviving the propagation fix. `HomeViewModel` now raises `HeadStoryChanged` when the newest visible story changes identity and `HomePage` snaps the carousel to the head. Re-verified on the emulator: the new story is the first card ([`04j-buttons-v2.png`](qa-shots/android/04j-buttons-v2.png)). Pinned by a test that also asserts the signal stays quiet on a plain in-place refresh |
| **Concurrency bug found and fixed during this QA** | `HomeViewModel.ActivateAsync` called `RefreshStoriesAsync` directly while a catalog-driven reload could be in flight — two passes mutating the same `ObservableCollection`, and the trailing trim threw `IndexOutOfRange` (caught by `FavoriteToggle_Persists`, not by inspection). All three list view-models now reach their reload **only** through the `CoalescingReloader`, which is the single writer; the reload methods are `private` so that cannot regress |
| Live propagation screenshot | [`android/04k-propagation.png`](qa-shots/android/04k-propagation.png) — the completion moment on the Library page |
| Player affordances | [`android/04l-player-buttons.png`](qa-shots/android/04l-player-buttons.png) — `‹ Close`, `■ Stop` / `↻ Replay` pills flanking the amber disc, Accent `PREMIUM →`. **PASS**; this screen went from three near-invisible text labels to three obvious controls without gaining a single filled button |

### Honest limits of this pass

- **iOS pressed states are unverified by eye.** Sim tap automation is TCC-blocked, so the iOS
  evidence is one launch screenshot proving pill geometry, stroke, colour and glyph
  presentation. The `TouchBehavior` press animations are code-parity with Android.
- **The three-pages-at-once claim is proven by tests, not by a device.** A phone shows one
  page at a time, so the device evidence is Library-live plus Home and Kid Shelf checked on
  arrival. `StoryPropagationTests` holds all three view-models alive simultaneously and never
  re-activates them, which is the property that actually matters.
- **Emulator weave time is not a device number.** ~33 minutes for a 10-minute story on an
  x86-hosted arm64 emulator says nothing about phone silicon; the M2 budget is unaffected by
  this pass and was not measured here.
- **`GhostButton` still exists** (unused for real actions, kept as a graceful fallback), and
  the project `CLAUDE.md` analytics invariant is still stale. Both are in `known-gaps.md`.

## Pass 11 — title variety + flowing prose (founder quality pass, 2026-07-31)

Founder verbatim: *"The story names are super similar to each other. Ensure more dynamic
stories, and longer sentences too — currently it sounds like a bunch of short sentences
stitched together."* Fixed measurably; harness gained `prose` and `titles` reports so the
numbers below are reproducible (`dotnet run --project src/tools/TaleWeaver.Harness -- prose --file story.txt`).

### Root causes

- **Titles.** Not the grammar (`outline.gbnf`'s title rule is a plain ASCII string — verified,
  unchanged). The outline prompt said only "The title is inviting and calm", and a 1.7B
  instruct model defaults hard to one shape: 4/4 old outline seeds and 2/2 old weaves produced
  "The Fox and the &lt;noun&gt;". Nothing threaded the shelf's existing titles into the prompt, so
  night after night converged. The template engine was worse by construction: 10 fixed title
  templates, 6 starting with "The".
- **Prose.** The 5–6 band prompt *mandated* "short, clear sentences" for every sentence, the
  scene prompt never asked for rhythm, and the polish pass smoothed transitions but never
  merged choppy runs — so scene-stitched output read as short statements in a row (old
  weave A: longest run of 4 consecutive <8-word sentences).
- **Lesson learned mid-pass:** the first fix attempt put literal example titles in the prompt
  ("Milo at the Tide Pools", …) — the model copied the example verbatim on 4/4 seeds. Final
  design uses abstract shape descriptions with ONE seeded shape hint per weave
  (`LlamaPrompts.TitleShapeHints`, 6 shapes) and the child's recent titles as negatives.

### Mechanism

- `StoryPipeline` fetches the profile's shelf and stamps the last ≤8 non-failed titles onto
  `ChildProfile.RecentTitles` (same transient seam as `ContentFilter`); the outline prompt
  injects them as "do not reuse their distinctive words / do not repeat their shapes".
- `LlamaStoryEngine` rotates a per-weave title shape hint (seed- and shelf-size-derived).
- `TemplateStoryEngine` composes titles from 8 grammatical shapes × seeded word pools on a
  forked rng (stories except the title stay seed-identical), with a redraw guard when a
  candidate shares >50% content tokens (Jaccard) with any recent title.
- Band prompts (5–6, 7–8): "vary sentence length like a storyteller … let sentences wander
  and curl with 'and' and 'while' and 'until' … then land a short one for effect; never
  three short sentences in a row". 3–4 keeps 5–10-word sentences with an occasional gentle
  stretch. Scenes ask for 2–3 concrete sensory details + one small gentle surprise; polish
  now explicitly merges choppy runs ("join them with 'and', 'while', 'until' or 'so that'")
  while keeping the pacing-punctuation channel (ellipsis/em-dash) intact.

### Metrics — old vs new (real Qwen3-1.7B weaves, age 6, foxes+ocean, 5-min, seeds 101/202)

| Metric | Old A (101) | Old B (202) | New A (101) | New B (202) |
|---|---|---|---|---|
| Title | The Fox and the Starlight | The Fox and the Moonlight | **Whisker's Tide** | **Sunny Day in the Forest** |
| Title pair Jaccard (A↔B) | 0.33, same shape | — | **0.00, different shapes** | — |
| Avg sentence length (words) | 13.0 | 14.1 | **18.0** | **14.7** |
| Sentence-length CV | 0.43 | 0.36 | 0.32 | 0.36 |
| Short (<8w) sentence share | 18% | 10% | **3%** | **9%** |
| Longest run of short sentences | **4** | 3 | **1** | **1** |
| Distinct-word ratio | 0.35 | 0.33 | 0.33 | 0.37 |
| Words / wall | 869 / n.c.¹ | 1026 / n.c.¹ | 1077 / 427 s | 806 / 304 s |

¹ Old-run wall times were not captured (the baseline log tail truncated the summary block);
word counts are `wc -w` on the stored `story.txt`.

Old outline titles (seeds 11/22/33/44): *The Fox and the Moonlight · The Fox and the Moonlit
Ocean · The Fox and the Ocean's Secret · The Fox and the Ocean Lantern* — one shape, four
times. New, same seeds: *Whiskers and Waves · Sunny Day in the Fox Garden · How Riley Found
the Ocean in His Garden · What Does the Fox Know About the Sea?* — four shapes.

Template fast path (`titles --count 30`): 30/30 distinct titles, 8 shapes, mean pairwise
Jaccard 0.023, max 0.500.

### New prose sample (weave A, opening)

> Riley the curious little fox ran through the forest, his paws brushing against mossy stones
> and the cool breeze whispering secrets to the trees. He had always loved the ocean — its
> vast blue waves, the way the sun lit up the water like a sparkling sea, and the soft sound
> of the tide rolling in. Today, he was determined to find the shimmering light that made him
> so happy.
>
> He chased it until the forest grew quiet, and the sky turned a gentle lavender color. Then,
> suddenly, he saw it — a faint glow on the shore, like a tiny star floating across the water.
> Riley stopped, his heart beating faster than usual.

### Verification

| Check | Result |
|---|---|
| Tests | **358/358 green** (340 at pass start; +18: title variety/determinism/guard, prose-rhythm floors per band incl. the 3–4 gentle ceiling, recent-title threading through the real pipeline + sqlite store, prompt-delta pins, metrics unit checks) |
| Builds | Android and iOS simulator — both **0 errors** (Core/Contracts/Harness-only change) |
| Safety | SafetyFilter untouched (pipeline gate passed on both weaves — a weave cannot complete flagged); independent lexicon grep over both new stories: clean. Age 3–4 band unchanged in spirit: short sentences, no flowing mandate, Brave-ish still tones down to Gentle |
| Grammar | `outline.gbnf` inspected, NOT the constraint — unchanged (ASCII lock kept) |

### Honest limits

- LLM-side evidence is 4 outlines + 2 full weaves on the Mac harness; shape rotation across a
  real multi-night shelf (negatives accumulating weave over weave) is modeled in tests but
  not yet observed on device. Tracked in `known-gaps.md`.
- CV on new weave A dipped slightly (0.43→0.32) because short sentences nearly vanished; the
  floor (≥0.3) still holds and the founder complaint was choppiness, not monotony of length.
- Old-vs-new used the same seeds, model, and stage code — only prompts and title threading
  differ — but n=2 weaves is a comparison, not a distribution.

# Generation-pipeline perf pass — 2026-07-31 (founder ask: parallelize/overlap the weave)

Three optimizations landed together (architecture §6 updated): **(1) overlapped scheduling** — polish per ~2-scene window, narration consumes polished windows from a channel while later scenes still generate (teaser still first); **(2) KV prefix caching** in `LlamaRunner` (`llama_memory_seq_rm`, suffix-only decode, cached-vs-fresh identity pinned by a model-gated test); **(3) poster off the critical path** — after the final scene's LLM work, in parallel with remaining narration. Sequential remains a tested strategy: forced via `TaleWeaverOptions.ForcedWeaveStrategy` / harness `--strategy sequential`, picked automatically under 6 GB RAM or a platform low-RAM flag, and used to resume legacy checkpoints.

## Mac harness, same weave (age 6, foxes+ocean, seed 4242, length 5), clean back-to-back runs

Measurement note: an earlier round of runs was polluted by concurrent builds/tests on the same machine (baseline read 452 s — cold mmap + Metal warmup + contention). The table below is the honest set: all four runs back-to-back on an idle machine, models warm, `weave --timings`.

| Run | Outline | Scenes·polish | Poster | Narration (RTF) | **Total** | Prompt tok (ingested / KV-cached) | Audio |
|---|---|---|---|---|---|---|---|
| Baseline (committed code: sequential, no KV) | 7.3 s | 48.9 s | 33.8 s | 187.0 s (0.38) | **276.9 s** | 8 059 / 0 | 495.5 s |
| Optimized, overlapped (all three) | 11.1 s | 72.8 s* | 50.7 s* | 216.7 s (0.55)* | **251.1 s** | 4 875 / 4 445 (48 %) | 392.7 s |
| Optimized, overlapped (repeat) | 9.6 s | 72.8 s* | 50.3 s* | 221.5 s (0.56)* | **255.1 s** | 4 875 / 4 445 | 392.7 s |
| Sequential fallback forced (+ KV) | 8.1 s | 43.8 s | 30.6 s | 181.0 s (0.37) | **263.5 s** | 4 254 / 3 805 (47 %) | 495.5 s |

\* Overlapped stage spans overlap in wall-clock (stage sum 351 s vs total 251 s → 100 s of work hidden by overlap); per-stage numbers include CPU contention between llama and sherpa.

Honest read of the Mac numbers:
- **Total −9.3 % overlapped vs baseline; sequential+KV −4.8 %.** On this Mac the LLM is fast (Metal) and TTS RTF is low (0.38), so narration is the long pole and there is little LLM time to hide — and contention raises TTS RTF 0.38→0.55 during overlap, eating much of the theoretical min(text, tts) saving. The overlap architecture is aimed at phone-shaped workloads (slow CPU LLM, RTF ≈ 1 TTS), where the hidden window is minutes, not seconds — see the emulator run below.
- **KV prefix cache: 47–48 % of all prompt tokens served from cache** (shared system prompt + per-stage preambles). At Mac prompt speeds that is ~10 s; at phone prompt speeds (tens of tok/s) it is minutes per weave.
- **Per-window polish produced a ~20 % shorter story on this fixture** (1 102 vs 1 393 words; audio 392.7 s vs 495.5 s) — same seed, same scenes, different polish granularity. The half-length guard held and the goodnight close appears exactly once (pinned by tests); flagged for the M3 rubric round to keep an ear on.
- Teaser-first flow unchanged in both strategies (teaser = first ~3 sentences of window 1, rendered before everything else).

## Android emulator (Trimix AVD raised to 8 GB RAM; models on device; profile Little One · Sage · 5 min; play nothing — file/DB verification only)

Two full real-AI weaves, back to back, same profile and length, near-identical output size (6.3 k chars, ~7:33 audio both):

| Run | Strategy actually taken | Wall | Story | Verification |
|---|---|---|---|---|
| Story 8 | **Sequential + KV** (first build's GC memory signal under-reported device RAM → guard tripped) | **≈ 10 m 40 s** (15:23:0x → 15:33:42) | "Luna's Lullaby", 453.6 s audio | State AwaitingApproval (approval on), audio present, **63 timestamps monotonic, indices 0..62**, checkpoint cleared |
| Story 9 | **Overlapped** (after fixing the signal to read `/proc/meminfo`) | **≈ 16 m 40 s** (15:37:53 → ~15:54:30) | "What did the fox say to the stars?", 454.1 s audio | State AwaitingApproval, audio present, **66 timestamps monotonic, indices 0..65**, checkpoint cleared; **overlap proven live on device**: teaser 7:39 in, then `story-9-part0/1/2.wav` appeared progressively while the weave ran (part0 10:11 in, part1 13:42, part2 14:42; 30–40 s poll granularity), poster thumb landed LAST (off the critical path), parts deleted after concat |
| Historical baseline (2026-07-30, same class of weave) | Sequential, no KV | **~26 min** (and 26–33 min band across runs) | — | — |

Emulator findings, honest:
- **Both new paths demolish the historical baseline** (10.7 / 16.6 min vs ~26–33 min). The big phone-side lever is the KV prefix cache — prompt ingestion at emulator CPU speeds was minutes per weave, now ~48 % of prompt tokens never re-decode.
- **On the 4-vCPU emulator, overlapped is SLOWER than sequential+KV** (16.6 vs 10.7 min): llama (4 threads) and sherpa (4 threads) fight over 4 cores — co-residency thrashes instead of parallelizing. There is no free lunch on a core-starved device; overlap only wins where the two engines get real concurrent silicon (Mac: Metal-GPU LLM + CPU TTS).
- Consequence, applied: `PickStrategy` now requires **≥ 6 GB RAM AND ≥ 6 cores** (plus not-lowRam) for overlap, else sequential — an 8 GB / 8-core phone overlaps, the 4-core emulator and small phones take the (faster for them) sequential+KV path. The crossover on real big.LITTLE silicon is an open M2 measurement (`known-gaps.md`).
- The run also flushed out a real device bug: Mono/Android's `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` reports a heap budget, not device RAM — the first 8 GB-emulator weave silently fell back to sequential. Core's default now reads `/proc/meminfo` on Android/Linux.
- Zero FATAL / AndroidRuntime errors across both weaves; cooldown QA lever (delete `tw.cooldown-*` keys) used between runs.

Tests after the pass: **373/373 green** (was 358; +15: overlap ordering with fakes, narration-before-last-scene, window-boundary checkpoint + mid-overlap resume with no re-synthesis, sequential fallback forced + low-memory/low-core picks, legacy-checkpoint sequential resume, progress monotonicity + early Narration row under overlap, producer-failure no-hang, exactly-one-goodnight, WindowPlan shapes, WAV concat exact/mismatch, window-polish prompts, KV cached-vs-fresh identity [model-gated]).


---

# Voice character — cozy mastering + Ember speaker audition — 2026-07-31 (founder: "always ending the sentence on a kinda high note… warm, cozy… awesome but almost too 'loud', too high-pitched — more like the Balance app narrator")

Scope: `AudioShaper` grew a **cozy mastering chain** (master switch `CozyMastering`, every element a tunable const), and Ember's Kokoro speaker was auditioned against 8 warmer/lower candidates on the same two-paragraph fox/pier fixture. Target character: low, warm, intimate, unhurried, meditation-narrator adjacent — soft sentence endings, no brightness/ring, generous headroom.

### The mastering chain (applies to every voice — narration, teaser, cached samples; `VoiceSampleVersion` 3 → 4)

1. **Pitch-down −0.6 semitone** via Catmull-Rom resampling (step 2^(−0.6/12) ≈ 0.9659), duration-compensated: the engine renders at `speed/PitchRatio` so the resample stretch cancels exactly — net pacing unchanged (measured +0.5% on the fixture, bound ±2%).
2. **High-shelf −3.5 dB @ 4 kHz** (RBJ biquad, S=1) — takes the bright sentence-ending ring off.
3. **Low-shelf +1.5 dB @ 200 Hz** — gentle chest-register warmth.
4. **Loudness re-targeted for bedtime:** whole-story gain now aims at **RMS −26 dBFS** under a **−6 dBFS peak ceiling** (was −1 dBFS peak-maxing). The ceiling always wins; MaxGain 3× still caps boost.

Chain is pure deterministic C# (`Biquad`, `Resample`, `MasterChunk`, `StoryGain` in `Prosody.cs`); legacy chain stays reachable (`harness tts --no-mastering`). Prosody gaps, trims and fades untouched.

### Speaker audition — same fixture, all through the NEW chain (`harness tts --sid N`)

| File (`qa-audio/`) | Speaker (sid) | Median F0 | Final-400 ms vs sentence mean | Centroid | Voiced | Energy CV | Duration |
|---|---|---|---|---|---|---|---|
| `audition-before-af_heart-legacy.wav` | **before**: af_heart (3), old chain | 200.0 Hz | **−3.6 st** (2 endings ≥ mean) | 2258 Hz | 75% | 0.59 | 25.0 s |
| `audition-af_heart.wav` | af_heart (3) — current, new chain | 196.7 | −3.6 st | 1692 | 79% | 0.57 | 25.1 s |
| `audition-af_aoede.wav` | **af_aoede (1) — the pick** | 174.5 | **−6.1 st** (all endings fall) | **1094** | **90%** | 0.54 | 25.5 s |
| `audition-af_kore.wav` | af_kore (5) | **147.2** | — | 1374 | 84% | 0.59 | 26.4 s |
| `audition-af_river.wav` | af_river (8) | 175.2 | — | 1444 | 85% | 0.58 | 26.7 s |
| `audition-af_jessica.wav` | af_jessica (4) | 200.0 | — | 1439 | 88% | 0.51 | 26.8 s |
| `audition-af_sarah.wav` | af_sarah (9) | 187.5 | — | 1673 | 85% | 0.52 | 25.2 s |
| `audition-af_nicole.wav` | af_nicole (6) | 158.9 | — | 1613 | **73%** | **0.79** | **32.6 s** |
| `audition-bf_emma.wav` | bf_emma (21) | 175.2 | — | 2149 | 83% | 0.56 | 26.2 s |
| `audition-bf_isabella.wav` | bf_isabella (22) | 198.3 | — | 1806 | 82% | 0.60 | 27.4 s |

All new-chain renders: peak −6.3 to −10.8 dBFS, RMS −25.8 to −25.9 dBFS (target −26), 0 clipped samples. Files trimmed to ≤25 s at prosody-gap boundaries (before/af_heart/af_aoede/af_sarah keep the full text incl. the soft "Goodnight, little fox").

### Provisional pick: **Ember = af_aoede (sid 1)** — pending founder audition

- **Warmest by a wide margin:** spectral centroid 1094 Hz vs af_heart's 1692 (and 2258 before mastering) — the "ring" is measurably gone.
- **Endings fall:** every sentence's final 400 ms sits 3.4–9.2 st **below** its sentence mean (mean −6.1 st); the before render had two endings at or above the mean (+0.43, −0.26 st) — the exact "high note" the founder heard. This is speaker-baked prosody: honest note — the −0.6 st shift and shelf lower everything uniformly but do **not** fix af_heart's terminal rises on their own; the speaker change does.
- **Full-voiced, not whispery:** 90% voiced frames (highest of the set), even energy (CV 0.54), pacing closest to current (25.5 vs 25.1 s).
- **af_nicole rejected on its own evidence** (the prior favourite): 73% voiced, energy CV 0.79, +30% duration — breathy ASMR delivery that drags story pacing.
- af_kore (lowest F0, 147 Hz) and af_river (narrowest pitch band) are the runners-up if af_aoede reads as too dark on the founder's ear. Revert = sid 3.

Willow (af_bella=2) and Sage (am_michael=16) keep their speakers; both now render through the new chain.

### Verification

| Check | Result |
|---|---|
| Tests | **387/387 green** (was 373; +14 `CozyMasteringTests`: resample length/pitch-shift accuracy (zero-crossing 425±2 Hz from 440), RenderSpeed duration compensation exact, shelf gains ±1 dB at target bands + mid-band unity, impulse stability/no-NaN/no-DC, end-to-end no-clip under the −6 dBFS ceiling, RMS targeting incl. MaxGain and ceiling-wins cases, legacy path when off, sample-exact determinism, pass-through/empty) |
| Duration compensation | Fixture af_heart: 25.00 s (legacy) → 25.12 s (cozy) = **+0.5%** (bound ±2%) |
| Harness | `tts` renders clean with mastering on; new flags `--sid N`, `--no-mastering` documented in help |
| Builds | Core + Harness + tests build 0 errors. **App heads (Android/iOS) currently fail on a pre-existing `HomePage.xaml` `OnPressDownLight` handler mismatch from a concurrent Views workstream (file timestamped mid-session, outside this pass's ownership) — nothing in this pass touches App/Views; re-verify once that workstream lands** |
| Determinism | `MasterChunk` byte-identical across calls (unit-pinned); chain is pure C# math, no platform intrinsics |

### Honest verdict

The numbers move the right way — centroid −35% vs the legacy render, endings −6.1 st, RMS parked at −26 dBFS with 20 dB of true peak headroom — but "cozy" is a founder's-ear call: the audition set exists so speaker (9 candidates, current included) and mastering (before-vs-after on the same speaker) can be judged **separately**. The terminal "high note" is baked speaker prosody — mastering alone tames brightness and loudness but not the rise; switching to af_aoede is what fixes the endings, and that choice is explicitly provisional until the founder listens.

---

## Pass 12 — background weave · loom hum · native players (2026-07-31)

Three founder asks landed in one pass: **the weave must keep going when the app is
backgrounded** (with a local "story ready" ping, zero server-side, ever), **the loom must hum
audibly while it weaves**, and **narration must run through the platform's native audio stack
so AirPlay and friends work**. Salvage note: the pass resumed an earlier agent's interrupted
work (`IWeaveBackgroundAnchor`, `LoomHum`, `LoomHumService`, `WeaveNotificationCopy` already
existed; nothing was wired, no platform code existed).

### What was built

| Piece | File |
|---|---|
| Android foreground service (`specialUse`, wake lock, "The loom" notification) | `Platforms/Android/WeaveForegroundService.cs` |
| Android anchor | `Platforms/Android/AndroidWeaveBackgroundAnchor.cs` |
| Android "becoming noisy" (headphones out → pause narration) | `Platforms/Android/NativeAudioAndroid.cs` |
| iOS grace window + `BGProcessingTask` + measured-grace logging | `Platforms/iOS/WeaveBackgroundCoordinator.cs` |
| iOS session / Now Playing / remote commands / route changes | `Platforms/iOS/NativeAudioIntegration.cs` |
| Manifest: `specialUse` + justification, FGS/WAKE_LOCK perms | `Platforms/Android/AndroidManifest.xml` |
| Info.plist: `UIBackgroundModes` (audio, processing) + BGTask id | `Platforms/iOS/Info.plist` |
| BGTask registration before launch completes | `Platforms/iOS/AppDelegate.cs` |
| Metadata + Now Playing + remote transport on the narration player | `Services/NarrationPlayers.cs` |
| Hum start/stop + anchor lifecycle | `Services/WeaveHostService.cs` |
| DI wiring (anchor, `LoomHumTrackProvider`, `LoomHumService`) | `MauiProgram.cs` |
| QA hook: `TALEWEAVER_QA_WEAVE=1` cold-starts a real weave | `App.xaml.cs` |

### Why `specialUse` and not `dataSync`

`shortService` caps at ~3 min and ANRs the whole app if the cap is blown — a 3–15 min weave
disqualifies it outright. `dataSync`'s documented use cases are transfer-shaped (upload,
download, backup-restore, import/export, "local file processing"); on-device LLM+TTS inference
is none of those, apps targeting Android 15+ get a cumulative **6 h / 24 h** budget on it with
an `onTimeout` → ANR contract, and Google publishes a migration guide steering apps off it.
`mediaProcessing` fits the narration stage only and carries the same budget. `specialUse` is
the documented catch-all, has no system timeout, and its price is a **manual Play review of a
free-form justification with a demo video** — which is honest work the Stage-7 compliance gate
owns, rather than mis-declaring the workload to dodge a review. The justification string is in
the manifest. Merged manifest verified: `foregroundServiceType="specialUse"`,
`PROPERTY_SPECIAL_USE_FGS_SUBTYPE` present, `targetSdkVersion="36"`,
`FOREGROUND_SERVICE_SPECIAL_USE` + `WAKE_LOCK` permissions merged.

### Android emulator (emulator-5554, API 36, targetSdk 36, models on device, profile Little One · Sage · 5 min)

| Check | Evidence |
|---|---|
| Foreground service runs for the weave | `dumpsys activity services`: `isForeground=true foregroundId=1914 types=0x40000000` — `0x40000000` = `FOREGROUND_SERVICE_TYPE_SPECIAL_USE` |
| Notification is the spec'd one | same dump: `channel=taleweaver.loom … flags=ONGOING_EVENT\|ONLY_ALERT_ONCE\|NO_CLEAR\|FOREGROUND_SERVICE\|SILENT … category=progress`; `android.title=Weaving Little One's story…`, `android.text=Scenes · stitch · polish · 35%` |
| Silent, low, un-dismissable | lands in the shade's **Silent** section (screenshot `qa-shots/android/05i-background-weave.png`) |
| Partial wake lock held, named | `dumpsys power`: `PARTIAL_WAKE_LOCK 'taleweaver:weave' ACQ=-3m17s LONG (uid=10231 pid=32426)` |
| The loom actually hums | `dumpsys power` also shows `PARTIAL_WAKE_LOCK 'AudioMix'` with `WorkChain{(10231), (1041)}` — the audio server holding a lock **on behalf of the app's uid** while the weave ran, i.e. the system agreeing audio was playing |
| Generation continues backgrounded **and with the screen off** | `mWakefulness=Asleep` throughout; notification text advanced `20% → 27% → 35% → 43%` across four 30 s polls |
| Update throttle | notification-posted events in logcat at 17:00:35 / 17:01:14 / 17:02:22 — ≥ 30 s apart, well inside the ≤ 1 / 10 s bar |
| Tap target | content intent → `MainActivity` (SingleTop) |
| **Weave COMPLETES in the background, screen off** | `mWakefulness=Asleep` from 17:24 to 17:38; notification text climbed `Scenes 45% → Narration 51 → 56 → 60 → 62 → … → 95 → 97%`, then at **17:38:26** notification **1913 "Tonight's story is ready — “Luna and the Moonlit Fox” — woven for Little One."** fired with the screen still asleep (`qa-shots/android/05j-ready-notification.png`) |
| Clean teardown | immediately after completion: foreground service gone (0 records), `taleweaver:weave` wake lock gone (0), loom notification 1914 gone (0) |
| Story is real and complete | `stories` row 10 — State=1 (Ready), 7 938 chars, **567.6 s (9:27) of narration**; `story-10.wav` 27.2 MB + teaser + `story-10-thumb-v8.png` on disk; `checkpoints` table empty |
| Reopen | Home shows "Luna and the Moonlit Fox" as the head carousel card with its poster, mini-bar reads "Story ready — tap for the teaser ▶", CTA correctly cooled down ("The loom rests — next weave at 18:08") |

#### Process-death test

| Step | Result |
|---|---|
| `adb shell am kill net.frostaura.taleweaver` mid-weave | **Refused — the process survived** (same pid). `am kill` only reaps cached/background processes, and the foreground service holds the app above that bar. That is the feature working, not a failed test |
| Forced kill instead (`run-as … kill -9 <pid>`) at Scenes/scene 5 | process gone; `WeaveForegroundService` record gone; **wake lock released cleanly** (0 held — no leak across an abrupt death); `checkpoints` row intact (`StoryId 10, Stage 2, SceneIndex 5`) |
| Relaunch | `TryResumeAsync` resumed the checkpoint: new pid 15769, foreground service back up (`types=0x40000000`), fresh wake lock, and the notification painted **`Scenes · stitch · polish · 45%` immediately** — i.e. it resumed where it died, not from zero. The weave then ran to completion as above |

#### One fix the device found

The first build subscribed the notification to `IWeaveHost.ProgressChanged`; during QA the shade sat on "Outline · 5%" while the SQLite checkpoint already read Scenes/scene 5. The cause was never isolated between "events marshalled through `MainThread.BeginInvokeOnMainThread` while the activity is destroyed" and "the stage genuinely reports rarely on a contended 4-vCPU emulator", so the dependency was removed rather than diagnosed: the service now **polls `IWeaveHost.CurrentProgress` on its own 10 s `Handler` tick** (the tick IS the throttle; an unchanged line is not re-posted). It also self-heals a service that resolves DI a moment before the MauiApp exists. Post-fix, the resumed weave painted its true 45% within a second of relaunch.

### iOS simulator (iPhone 17 Pro, iOS 26.1, models on device, real Qwen3 + Metal)

Simulator tap automation is TCC-blocked, so the weave was cold-started with the new
`SIMCTL_CHILD_TALEWEAVER_QA_WEAVE=1` hook and the app was backgrounded by launching Settings.

| Check | Evidence (`Console.WriteLine`, captured via `simctl launch --console-pty`) |
|---|---|
| Session category/mode/route | `TWAudio session configured — category=AVAudioSessionCategoryPlayback mode=AVAudioSessionModeSpokenAudio route=[Speaker:Speaker]` |
| Hum composed + cached per palette family | `loom-EmberSky.wav` appeared in the app container's `Library/media` (profile's first interest is Foxes → EmberSky), rendered off-thread inside the 2 s fade-in delay |
| Backgrounded mid-weave, assertion taken | `TWBg backgrounded mid-weave; assertion=9; iOS says unlimited (audio session alive) remain` |
| **Grace budget actually granted** | `unlimited` — with the hum playing, `BackgroundTimeRemaining` never counts down. The 5 s sampler kept firing for the whole background period (`grace +75s elapsed; unlimited …`), which is itself proof the app was **not suspended** — a suspended app's timers do not run |
| `BGProcessingTask` | `TWBg BGProcessingTask submit refused: BGTaskSchedulerErrorDomain error 1` — the simulator refuses to schedule background processing. Expected, and exactly why no copy promises this path |
| Still alive after **33 minutes** backgrounded | `TWBg grace +1989s elapsed; unlimited (audio session alive) remain`, process at 35% CPU with the LLM resident (RSS 1.2 GB). Without the audio session iOS would have suspended it inside a minute |
| Background **completion** on iOS | **NOT reached in this pass** — 39 min in, the simulator was still on the outline stage (SQLite untouched since launch). Survival is proven; the completion notification on iOS is not |
| Why so slow — and a warning | `sample` of the process shows `ggml_graph_compute_thread` / `ggml_cpu_extra_compute_forward` / `ggml_gemv_q` with `llama_grammar_element`: the simulator runs llama.cpp on the **CPU** backend even though the Metal kernels compile at load. So it was working, not wedged — and **simulator weave timings must never be quoted as a performance signal** for iOS |

**Verdict on the iOS background weave:** it works, and it works *because the loom hums*. That
is the honest framing — not "iOS now supports background generation" but "an audible feature
the parent can switch off is what keeps it alive, with checkpoint/resume underneath". The
`BeginBackgroundTask` fallback's real budget stays unmeasured precisely because the primary
path never yielded to it; measuring it needs a device run with the Music slider at zero.

### Native players / AirPlay

| Check | Evidence |
|---|---|
| Android media session exists and is the media-button owner | `dumpsys media_session`: `Media button session is net.frostaura.taleweaver/androidx.media3.session.id.…`, `active=true`, `actions=7339997` |
| Story metadata reaches it | `metadata: size=4, description=Luna's Lullaby, Sage, null` — story title + narrator persona, from the newly-set `MetadataTitle`/`MetadataArtist` |
| Output-route picker present | the shade's media card renders a **"This phone"** route chip with transport + scrubber (`qa-shots/android/05i-background-weave.png`) — that chip is the Cast/Bluetooth route picker |
| Artwork | **Fixed and confirmed.** The first dump had no cover: `MetadataArtworkUrl` was being handed a `file://` URI. Changed to a plain filesystem path (the toolkit normalizes file paths and reads them with a FileStream) — metadata grew from `size=4` to `size=6` and the system media card now renders the story's poster as its background (`qa-shots/android/05k-now-playing-airplay-surface.png`). `description=… , null` in `dumpsys` is only the unset DISPLAY_ICON slot; the ART key carries the bitmap |
| **External transport actually drives our player** | `adb shell media dispatch pause` — the same media-key path a Bluetooth headset or car button uses — moved the session from `state=PLAYING(3)` to `state=STOPPED(1)`. The app was backgrounded at the time |
| Live playback state | `PlaybackState {state=PLAYING(3), position=5993, speed=1.0, actions=7339995}` — position and rate tracked, so an external scrubber has real data |
| iOS session | `Playback` + `SpokenAudio` + `AllowAirPlay\|AllowBluetoothA2DP`, logged above |
| iOS Now Playing / remote commands | code compiles and the load path calls `SetNowPlaying` → `DumpNowPlaying` and `RegisterRemoteCommands`. **Not observed with a story loaded** — the simulator weave had not produced audio before the pass closed, and sim tap automation is TCC-blocked, so no story could be opened by hand |
| Live-TTS path | deliberately publishes **no** Now Playing entry and clears any stale one — an utterance engine has no seekable timeline to hand a car head unit |

**Unproven without hardware:** no AirPlay receiver, CarPlay head unit or Bluetooth speaker has
been in the loop. The simulator has no AirPlay target and the emulator no Bluetooth sink, so
everything above is "the system accepted our declarations", not "a speaker played it".

### Build trap found (cost ~15 min)

`_CompileAppManifest`'s incremental inputs are `obj/…/_CompileAppManifest.inputs` (a property
digest) and the resizetizer partial plists — **`Platforms/iOS/Info.plist` itself is not an
input**. Editing it and rebuilding silently ships the previous plist and the app launches
without the new keys. Fix: `rm obj/Debug/net10.0-ios/*/AppManifest.plist` before rebuilding,
and always confirm with `plutil -p <app>/Info.plist`. Recorded in `known-gaps.md`; belongs in
`memory/gotchas.md` too (outside this pass's ownership).

### Raw evidence

The full iOS console capture (486 `TWBg`/`TWAudio` lines, llama.cpp load, the whole
background period) is preserved at `/private/tmp/taleweaver-ios-background-weave-2026-07-31.log`.
Android screenshots: `qa-shots/android/05i-background-weave.png` (loom notification, silent
section, backgrounded), `05j-ready-notification.png` (story-ready ping fired with the screen
asleep), `05k-now-playing-airplay-surface.png` (system media card with poster artwork,
narrator, transport and the output-route picker).

---

# Anticipatory haptics + the dead narrator-preview ring — 2026-07-31 (founder: "add much more haptics… make them feel anticipatory rather than reactive", then "the Narrator Preview buttons still do nothing… there's NO HAPTIC when I press them either")

Two findings, and they turned out to be the same investigation: the missing haptic was the *diagnostic* that proved the preview gesture was never being recognized.

## Part 1 — the anticipatory-haptics sweep

The principle is now written into [`mockup-spec.md`](mockup-spec.md): a haptic fires at **gesture recognition** — pointer-down for taps, threshold-crossing for holds and drags — always **before** the command, navigation, animation or load it acknowledges. The command-level haptic that follows the same gesture is *coalesced away*, not deleted.

Mechanism (Core-tested, so the windows are not folklore):

| Piece | Where | What it does |
|---|---|---|
| `PressLight/PressMedium/PressSelection` | `App/Services/Haptics.cs` | Fire at recognition; arm the long refractory |
| `LightWindowMs` 90 · `MediumWindowMs` 90 · `SelectionWindowMs` 60 · `PressRefractoryMs` 350 | `Core/Services/HapticTickLimiter.cs` | A passed press swallows the follow-up command haptic; a *new* press is gated only by the short window, so a new finger is always answered |
| `PressHaptics.OnPressed` attached property | `App/Services/PressHaptics.cs` + `Styles.xaml` | Every `Button` answers at pointer-down with no per-page markup. `Button.Pressed` is not raised when disabled ⇒ `IsEnabled` respected by construction |
| `PressHaptics.Touch(owner, kind, onTapped, listenOnly)` | `App/Services/PressHaptics.cs` | The same contract for controls built in C#; both halves gated on `owner.IsEnabled`. `listenOnly` adds only the haptic where an existing pan/swipe recognizer must survive |
| `UxTrace.Action(...)` | `App/Services/UxTrace.cs` | Debug breadcrumb so ordering is *provable*, not asserted |

Sites swept this pass (the earlier pass covered Home/Library/Settings/Shelf, `LengthTrackView`, `ToggleSwitchView`, `Styles.xaml`, `ShellNavigator`): `ContentFiltersPage` (radio rows → Selection), `OnboardingProfilePage` (age numerals, interest toggles → Selection), `OnboardingVoicePage` (voice rows → Selection, ▶ rings → Light, series pair → Selection, premium hint → Light), `OnboardingRulesPage` ("Start my 7 free nights" → **Medium**, the threshold action), `PremiumPage` (drag handle, Debug unlock row → Light; the unlock's `Celebrate` stays the terminal outcome *after* it), `WeaveSheetHost` (scrim → Light, mini-bar → Light, grab zone → **Tick at first touch**), `BottomShelfHost` (scrim → Light, grab zone → Tick at first touch), `RecipeShelfView` (chips/rows → Selection, ▶ rings → Light), `PlayerPage` (play/pause disc, drag handle, locked Music row → Light; existing scrub Ticks and approval `Celebrate` untouched), `MixTrackView` (grip Tick at first touch, silent when disabled).

**Drag controls tick at first touch, not first movement.** A `PanGestureRecognizer`'s `Started` only arrives after the slop threshold, so every track and handle felt dead for the first few millimetres. `LengthTrackView` was fixed in the earlier pass; `MixTrackView`, the Player scrub area, and both sheet grab zones are fixed here.

### Emulator proof (emulator-5554, Debug APK, `logcat` timestamps verbatim)

The rule is "`TWHaptics` precedes the matching `TWAction`". Four sites plus the sheet handle:

```
SITE 1 — Home, long-press context menu
16:56:29.895 D/TWHaptics: Light@press          ← finger lands on the card
16:56:30.512 D/TWHaptics: Medium               ← long-press threshold recognized
16:56:30.517 D/TWAction : ContextMenu show     ← menu built 5 ms AFTER the haptic

SITE 2 — Library row tap → Player
16:57:02.898 D/TWHaptics: Light@press          ← pointer-down
16:57:02.903 D/TWHaptics: Light~coalesced      ← the navigator's own Light, swallowed
16:57:02.903 D/TWAction : Nav player
16:57:02.946 D/TWHaptics: Light~coalesced      ← the page-open Light, also swallowed

SITE 3 — Weave CTA (threshold action ⇒ Medium)
16:57:21.933 D/TWHaptics: Medium@press
16:57:22.018 D/TWHaptics: Medium~coalesced     ← the VM's weave-start Medium, swallowed
16:57:22.107 D/TWAction : Weave start          ← 174 ms AFTER the haptic

SITE 4 — Kid Shelf, 2-second lock hold
17:08:48.710 D/TWHaptics: Light                ← grip at touch-down
17:08:49.228 D/TWHaptics: Tick                 ← 0.5 s
17:08:49.732 D/TWHaptics: Tick                 ← 1.0 s
17:08:50.242 D/TWHaptics: Tick                 ← 1.5 s
17:08:50.729 D/TWHaptics: Success              ← threshold crossed (terminal, never coalesced)
17:08:50.743 D/TWAction : Nav //home           ← exit 14 ms AFTER the haptic

WEAVE SHEET — grab zone, tap-and-hold without moving
16:58:00.229 D/TWHaptics: Tick                 ← FIRST TOUCH, before any movement
16:58:00.480 D/TWHaptics: Light                ← the collapse commit
```

Every ordering is haptic-first, and the `~coalesced` lines are the mechanism working: the press-down thump is the only one felt.

## Part 2 — root cause of "the Narrator Preview buttons do nothing"

**The founder was right that the missing haptic was the tell, and it pointed at a binding bug, not a hit-target one.**

First capture, on the shelf's ▶ ring for the *already-selected* narrator:

```
17:00:00.610 D/TWHaptics: Light@press          ← the touch DID reach the ring
17:00:00.638 D/TWHaptics: Light~coalesced
17:00:00.644 D/TWAction : Nav player           ← ...and then opened a STORY
```

The press haptic fired, so the gesture was arriving — but `PlaySampleCommand` never ran, and the tap fell through to the Home carousel *underneath the shelf* and opened a story instead.

**Root cause:** in `RecipeShelfView.xaml` every command inside the `BindableLayout` item templates was bound with `{Binding Source={RelativeSource AncestorType={x:Type vm:RecipeShelfViewModel}}, Path=…}`. **That binding does not resolve inside a `BindableLayout` DataTemplate** — the ancestor chain is not established when the binding is applied. The `Command` was therefore **null**, which does two things at once: the tap does nothing, *and* a `TapGestureRecognizer` with a null command does not consume the touch, so it passes through to whatever is behind. This affected `PlaySampleCommand`, `SelectVoiceCommand` **and** `ToggleInterestCommand` — the whole shelf was inert, not just the rings. This is the third time this codebase has been bitten by "bindings don't inherit the DataTemplate/behavior BindingContext" (`memory/gotchas.md`).

Contributing factors, all fixed:

1. **No haptic at all on the already-selected voice.** `PlaySampleAsync` → `SelectVoice(option)` only fired `Selection` when the voice *changed*. Tapping ▶ on the current narrator produced literally zero feedback — exactly what the founder was pressing. Press-down `Light` now fires regardless.
2. **`[RelayCommand]` refused repeat taps.** The generated async command reports `CanExecute=false` for its whole duration, and a preview lasts as long as render + utterance. Every tap after the first was silently refused *by the command layer*. Now `AllowConcurrentExecutions = true` with cancel-and-replace (`VoicePreviewRunner`).
3. **40 dp target, under the 44 dp minimum**, with the ring `Border` sitting on top with no recognizer of its own (so it would swallow any touch that landed on it). The visible ring is unchanged at 40 dp but is now `InputTransparent` inside a **48 dp transparent target** that owns the gesture. Same treatment in both places.
4. **Both call sites swallowed every exception** (`catch (Exception) { }`), which is *why this was reported fixed twice while still failing* — a broken preview was indistinguishable from a dead button. Every step now traces under the tag `TWVoice`, and failures stay non-fatal but are no longer invisible.
5. **No perceived responsiveness.** `VoiceOption.IsPreviewing` flips true synchronously on tap; the ring goes amber and dims to 0.45 until the sample is handed to the player.

### Verified end to end on the emulator

```
COLD (first render of this voice — model warm-up included)
17:06:52.885 D/TWHaptics: Light@press                                    ← haptic FIRST
17:06:52.959 I/TWVoice  : preview requested voice=Sage                   ← command now RUNS
17:06:52.979 I/TWVoice  : sherpa: rendering Sage → …/voice-samples/sage-v4.wav
17:07:42.828 I/TWVoice  : sherpa: rendered Sage → '…/sage-v4.wav' exists=True
17:07:42.839 I/TWVoice  : rendered voice=Sage file=…/sage-v4.wav
17:07:42.932 D/TWVoice  : player: started 347684 bytes …/sage-v4.wav
17:07:42.938 I/TWVoice  : playback handed off voice=Sage

WARM (file cache hit) — 166 ms from finger to audio
17:08:04.268 D/TWHaptics: Light@press
17:08:04.321 I/TWVoice  : preview requested voice=Sage
17:08:04.325 I/TWVoice  : sherpa: cache hit …/sage-v4.wav
17:08:04.427 D/TWVoice  : player: started 347684 bytes …/sage-v4.wav
17:08:04.434 I/TWVoice  : playback handed off voice=Sage
```

Audio genuinely plays — `dumpsys audio` during the preview:

```
AudioPlaybackConfiguration piid:135 deviceIds:[2] type:android.media.MediaPlayer
  u/pid:10231/8814 state:started attr:usage=USAGE_UNKNOWN … sampleRate=24000
```

`state:started` on TaleWeaver's uid at Kokoro's 24 kHz.

**Honest limit worth flagging:** the *cold* render took **49.8 s on the emulator** (neural TTS on an emulated CPU; a real device is several seconds). The dimmed ring acknowledges the tap immediately and the cache makes every subsequent tap instant, but a first-tap wait that long is a real UX problem on slow hardware and is not solved by feedback alone — pre-warming the three samples in the background is the obvious follow-up and is **not** done in this pass.

The onboarding ▶ ring shares the `VoicePreviewRunner`, the 48 dp target and the press haptic; it is compile-verified and behaviourally identical, but it was not reachable on this device (onboarding is complete) so it is **not** separately log-proven.

## Verification

| Check | Result |
|---|---|
| Tests | **428/428 green** (+11 this pass: `HapticPressCoalescingTests` covering press-arms-refractory, refractory expiry, new-press-never-silenced, two-sources-one-thump, command re-arms only the short window, blocked calls don't push the window, per-kind windows, window < refractory invariant, custom refractory; plus two terminal-`Celebrate`-is-last ordering tests. Remaining delta vs. the 398 this pass produced is the concurrent mastering workstream's own additions) |
| Android build | `-f net10.0-android` — **0 errors** |
| iOS sim build | `-f net10.0-ios -p:RuntimeIdentifier=iossimulator-arm64` — **0 errors** |
| Device proof | Debug APK (`EmbedAssembliesIntoApk`, `adb install -g -r`) on emulator-5554; all excerpts above are unedited `logcat -v time` |
| Disabled controls | No haptic — `Button.Pressed` is not raised when disabled; `PressHaptics.Touch` and `MixTrackView`'s grip both gate on `IsEnabled` |
| Passive scroll | Unchanged — no haptic source added to any scroll path |
