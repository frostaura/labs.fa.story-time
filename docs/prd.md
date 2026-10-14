# TaleWeaver — Product spec stack

## LAYER 1 — One-pager

**Product**: TaleWeaver — the phone that tells your child a brand-new bedtime story, every night, completely offline.

**Who**: parents of kids aged 3–8 who want a calm, screen-light bedtime ritual and do not trust cloud AI with their children's names, interests or voices.

**Problem**: bedtime story supply is finite (books repeat, parents are tired); every existing AI-story product is a cloud service with accounts, subscriptions, data collection and latency.

**Bet**: on-device generation is now good enough. A bundled ~1.4 GB stack (1.7B LLM + neural TTS + procedural art) can weave a coherent, safe, gently-narrated 5–15-minute story on the phone itself. Nothing leaves the device — that privacy stance is the marketing, the compliance posture, and the moat rolled into one.

**Shape**: .NET MAUI, iOS + Android, zero server, zero auth. One tap to weave. Teaser-approval for parents. Biometric-locked settings, browse-only Kid Shelf, 30-minute story cooldown. Series mode with a persistent Story Bible, or nightly one-shots.

**Not**: a chat app (kids never free-text to a model), a subscription treadmill (no auto-renew, per portfolio doctrine), a tablet babysitter (cooldown is a feature, not a bug).

## LAYER 2 — PRD (M1–M2 scope)

**Experience principles**: instant gratification on first open (a pre-woven welcome story is on the shelf before any setup); sensible defaults everywhere (age 5, three interests pre-toggled, Ember, 10 min, series); two taps from open to weaving; everything gentle — animation, voice pacing (0.92×), story endings ("Ends gently. No cliffhangers, ever.").

**Screens**: the 8-screen Hearth contract in [`design/mockup-spec.md`](design/mockup-spec.md) — Onboarding ×3, Home, Generating, Player, Kid Shelf, Parent settings. Plus a `library` (All →) list reachable from Home.

**Functional requirements**
1. Profile: 1+ child profiles (name, age 3–8, interests from a curated topic list); active profile drives Home.
2. Weave: one story at a time; 4-stage pipeline with honest progress; checkpoint/resume across app restarts; local notification when ready; cooldown 30 min after completion (Premium 15 — stubbed).
3. Approval step (default ON): a ~20 s teaser plays for the parent before the full story is playable by the child.
4. Player: file-backed audio with real seek, background playback, lock-screen art/metadata; 4-layer gyro-parallax poster; series recap line; "Continue the series · +2 min" appends a coda scene.
5. Kid Shelf: browse/replay only; long-press-2s lock pill to exit; no destructive or generative actions.
6. Parent settings: biometric on every entry; toggles (approval, notifications — analytics was removed entirely by founder decision 2026-07-31); profile management; content filter level (Gentle — the only level at M1); storage usage with real bytes.
7. Series: Story Bible (characters, places, last-episode recap) persisted per profile and carried into the next episode's prompts.
8. Safety: layered by construction (see architecture §7); a story that fails its filter after retries is never shown — the weave reports a gentle "the loom snagged, try again" state.

**Non-functional**: 10-min story E2E ≤10 min on iPhone-17-class (M2 gate); app usable entirely offline from first launch; no data leaves the device; storage self-reported and prunable (delete story removes audio+art).

**Out of scope M1/M2**: billing, music-under-narration, lock-screen art extras, multi-language, tablet layouts, story sharing beyond OS share-sheet export of the poster+text.

## LAYER 3 — Monetization notes (deferred to M4)

Design shows: 7 free nights, then Premium (15-min stories, lock-screen art, music bed, 15-min rests). Doctrine constraints: one-off or non-renewing IAP only — never auto-renew. Decision deliberately deferred until M3 quality evidence exists (money-gate discipline); until then the trial counter and UPGRADE sheet are local stubs and no price is printed anywhere in the app.
