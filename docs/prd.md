# StoryTime — Product spec stack

## LAYER 1 — One-pager

**Product**: StoryTime — the phone that tells your child a brand-new bedtime story, every night, made entirely on the device.

**Who**: parents of kids aged 3–8 who want a calm, screen-light bedtime ritual and do not trust cloud AI with their children's names, interests or voices.

**Problem**: bedtime story supply is finite (books repeat, parents are tired); every existing AI-story product is a cloud service with accounts, subscriptions, data collection and latency.

**Bet**: Apple's on-device models are now good enough. Apple Foundation Models write the story, Apple speech synthesis narrates it through StoryTime's own soothing chain, and a procedural engine paints its poster — a coherent, safe, gently narrated 5–15-minute story made on the phone itself, with no model files bundled or downloaded. Nothing leaves the device; that privacy stance is the marketing, the compliance posture and the moat in one.

**Shape**: standalone .NET MAUI app for iOS 27+. No server, no account; the only network use is the App Store for the optional Premium purchase. One tap to make tonight's story. Teaser approval for parents. Face ID-locked parent settings, a browse-only Kid Shelf, 30 minutes between stories. Series mode with a persistent Story Bible, or nightly one-shots.

**Not**: a chat app (kids never free-text to a model), a subscription treadmill (no auto-renew, per portfolio doctrine), a tablet babysitter (the wait between stories is a feature, not a bug).

## LAYER 2 — PRD

**Experience principles**: instant gratification on first open (a ready-made welcome story is on the shelf before any setup); sensible defaults everywhere (age 5, three interests pre-toggled, 10 min, series); two taps from open to making a story; everything gentle — animation, voice pacing, story endings ("Ends gently. No cliffhangers, ever."). Copy follows [`design/copy-glossary.md`](design/copy-glossary.md).

**Screens**: the Hearth contract in [`design/mockup-spec.md`](design/mockup-spec.md) — Onboarding ×3 plus the storyteller-voice step, Home with the making sheet, Player, Kid Shelf, Parent settings, Content filters, Library and Premium.

**Functional requirements**
1. Profile: 1+ child profiles (name, age 3–8, interests from a curated topic list); the active profile drives Home.
2. Making: one story at a time; four steps (Planning · Writing · Picture · Voice) with honest progress; checkpoint/resume across app restarts; local notification when ready; 30 minutes between stories (Premium 15).
3. Approval step (default ON): a ~20 s teaser plays for the parent before the full story is playable by the child.
4. Player: file-backed audio with real seek, background playback, lock-screen art and metadata; four-layer tilt-parallax poster; series recap line; "Continue the series · +2 min" (currently a design-state stub — see [`known-gaps.md`](known-gaps.md)).
5. Kid Shelf: browse and replay only; long-press lock pill to exit; no destructive or generative actions.
6. Parent settings: Face ID (falling back to passcode) on every entry; toggles (approval, notifications — analytics was removed entirely by founder decision 2026-07-31); profile management; content filter level (Gentle / Brave-ish); storage usage with real bytes.
7. Series: Story Bible (characters, places, last-episode recap) persisted per profile and carried into the next episode's prompts.
8. Safety: layered by construction (see [`architecture/overview.md`](architecture/overview.md) §7); a story that fails its filter after repair is never shown — the sheet says gently that the story didn't finish and to try again.
9. Apple Intelligence off or unavailable: the deterministic template engine writes the story instead, and Home shows a short notice when the parent can turn Apple Intelligence on.

**Non-functional**: a 10-minute story made end to end in ≤ 10 minutes on an iPhone 17-class device; usable entirely offline from first launch; no data leaves the device; storage self-reported and prunable (deleting a story removes its audio and art).

**Out of scope**: Android, multi-language, tablet-specific layouts, story sharing beyond OS share-sheet export of the poster and text.

## LAYER 3 — Monetization

Premium is one non-consumable in-app purchase (`net.frostaura.storytime.premium`), StoreKit 2 on the device: paid once, no subscription, no account, no server. It unlocks 15-minute stories, the music bed under narration and a 15-minute (instead of 30-minute) wait between stories. Seven free nights are counted from first run. Doctrine constraint: one-off or non-renewing purchases only — never auto-renew. Purchase flow, restore and the founder's App Store Connect step are in the [README](../README.md).
