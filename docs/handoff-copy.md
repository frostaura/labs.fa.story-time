# Copy handoff — background weave + loom hum (2026-07-31)

Proposed copy changes from the background-generation pass. **Nothing here has been applied**:
`Core/ViewModels/WeaveSheetViewModel.cs` is owned by a parallel workstream. The orchestrator
owns the merge.

## Context — what actually changed under the copy

| Platform | Before this pass | After |
|---|---|---|
| Android | Weave ran only while the app was foregrounded; a background/screen-off weave stalled and relied on checkpoint-resume when the parent reopened the app. | `WeaveForegroundService` (`specialUse` type) + partial wake lock keep the pipeline running through backgrounding, screen-off and activity destruction. A silent LOW-priority "The loom" notification shows live progress; the story-ready ping fires from the background. |
| iOS | Suspended on background; resumed on foreground. | The loom hum (a real, audible ambient loop) plays through a `Playback` audio session with `UIBackgroundModes: audio`, so the weave **keeps running** while it hums. If the parent silences it, a `BeginBackgroundTask` grace window checkpoints cleanly, and an opportunistic `BGProcessingTask` may resume it later. |

## 1. Android hint — no change needed

```
BackgroundHintAndroid = "Close the app if you like — we'll send a gentle ping when it's ready."
```

This line was flagged in `docs/known-gaps.md` as over-promising ("Android weave is
foreground-lifecycle only"). **It is now true as written.** No edit required — the gap closed
under the copy rather than the copy bending to the gap.

## 2. iOS hint — proposed change

Current:

```
BackgroundHintIos = "Come back anytime — we'll pick up right where we left off."
```

Proposed:

```
BackgroundHintIos = "The loom hums while it works — leave it playing and it keeps weaving."
```

Rationale, and the honesty constraints the wording has to satisfy:

- It must be **conditional on the hum**, because the mechanism is. Silence the hum (Music
  slider to zero, Control Center stop, an incoming call taking the audio session) and iOS
  suspends the app exactly as before.
- It must **not** promise a background ping. The `BGProcessingTask` resume path is
  opportunistic — iOS decides if and when it runs, and the simulator refuses to schedule it
  at all. Nothing in the product copy may depend on it.
- It must not mention "background", "keep-alive" or anything that reads as a workaround. The
  hum is a feature the parent hears; the copy should describe the feature, not the mechanism.

### Fallback wording, if a conditional line reads as too fussy for the sheet

```
BackgroundHintIos = "Leave the loom humming and it keeps weaving — or come back anytime and
                     we'll pick up right where we left off."
```

Longer, but it covers both states in one line and the second half is the existing, already
approved sentence.

### If the orchestrator prefers zero change

Keeping the current iOS line is defensible: it is still true (resume works), just
under-sells. There is no correctness problem with leaving it alone — only a missed
reassurance.

## 3. Not proposed, deliberately

- **A "we'll ping you on iOS too" line.** True only when the hum is playing or iOS happens to
  grant a `BGProcessingTask`. Unpromisable.
- **Any mention of a wake lock, foreground service or battery on Android.** The system's own
  notification already discloses the service; duplicating it in-app reads as an apology.
- **Naming the hum in Settings.** The Music slider already governs it (slider ≈ 0 is a
  per-weave opt-out). A separate toggle would imply the hum is optional infrastructure rather
  than part of the weave.
