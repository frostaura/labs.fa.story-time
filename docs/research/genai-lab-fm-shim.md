# Foundation Models shim: Mac end-to-end lab (2026-10-14)

This lab measured the shipped text path end to end on a Mac: the real C# `AppleFoundationStoryEngine` driving the real Swift `SEFMEngine` shim. The shim sources are compiled for macOS, and the run covers outline → scenes and digests → per-scene polish → poster. The design recipe is in [`foundation-models.md`](foundation-models.md). This file records what running that recipe actually produced, and which settings changed as a result.

Reproduce with `src/native/lab/run-fm-lab.sh 5`. Each run writes a full report to `.tmp/fm-lab/`, including the outline JSON, the raw and polished scenes, the poster spec and the ladder events.

## Setup

| | |
|---|---|
| Host | macOS 27, Apple silicon, Apple Intelligence on |
| Model | `SystemLanguageModel.default`, variant "AFM 3 Core Advanced", `contextSize` 8192 |
| Shim | `src/native/StoryTimeNative/Sources/FoundationModels/*.swift`, compiled with `swiftc -swift-version 6 -warnings-as-errors -target arm64-apple-macos27.0` |
| Bridge | JSON-lines pipe (`src/native/lab/FMLabHost`), with `FmLab.Console/PipeBridge.cs` implementing `IFoundationModelsBridge` |
| Profile | Mila, age 5 (5–6 band), interests Ocean and Stars, 5-minute length (5 scenes), one-shot, seed 20261014 |

## Timings

All times are wall-clock seconds measured from C#. Each scene time includes that scene's guided digest.

| Stage | Run A: polish on | Run B: polish off (shipped default) | Run C: polish off, with simulator generating concurrently |
|---|---|---|---|
| Outline (guided, 5 scenes and bible) | 15.0 | 14.8 | 23.6 |
| Scene 1 | 8.2 | 8.9 | 12.8 |
| Scene 2 | 7.1 | 8.7 | 11.3 |
| Scene 3 | 7.6 | 6.7 | 8.6 |
| Scene 4 | 8.0 | 8.7 | 11.5 |
| Scene 5 | 8.6 | 7.0 | 9.1 |
| Polish (3 windows, per scene) | 30.0 | 0 | 0 |
| Poster (guided enum choice) | 1.3 | 1.3 | 1.7 |
| **Total** | **85.8** | **56.1** | **78.7** |
| Words | 1,009 | 869 | 869 |

What the timings show:

- **Text work is about 1 minute for a 5-minute story on a Mac.** Each scene takes 7–9 s with its digest, so scene 1 is ready about 24 s after the user taps, outline included.
- **Generation is serialized system-wide.** Run C overlapped with the iOS simulator making a story through the same system model, and every stage slowed by 30–60%. Running stages in parallel gains nothing.
- **No numbers yet for an A18/A19 iPhone.** The device run is still owed.

## Decisions taken from the runs

1. **Per-scene polish is off by default** (`AppleFoundationOptions.PolishScenes = false`).
   - It added about 30 s, roughly 50% of total time.
   - It merged sentences into longer run-ons.
   - It appended recycled closing lines ("where every step brings new magic and every breath feels like a soft promise…" appeared 3 times).
   - It made middle scenes end in sleep.
   - When enabled, it now only accepts a rewrite whose length is within 0.75–1.25× the original.
2. **The scene prompt now asks for a sentence count**, about target words ÷ 18, each sentence under 35 words. Middle scenes are told that nobody falls asleep yet.
   - Before this change, with only "flowing prose" requested, every scene came back as one 150–180-word sentence, and every middle scene ended with the characters drifting off.
   - The age-banded instructions ask for 20–30-word sentences, and the model overshoots that.
3. **`ProseGuards.SplitRunOns` cuts any sentence over 45 words** at ", and / while / so / until" clause joints.
   - In an iOS 27 simulator story, the prompt fix alone still produced a single-sentence opening scene (5–6 band).
   - With the cut in place, the final text measured 35 sentences, averaging 24.9 words, with a maximum of 44.
4. **The previous scene's last two sentences are passed as context only**, with "do not repeat or reword". Opening sentences that repeat that tail are then stripped (5-gram overlap ≥ 0.6), and short stock lines already used in the story are removed and listed as "do not reuse".
5. **The model uses the house-banned words on its own.** In one run it used one of them to describe the characters' voices. `ProseGuards.HouseVocabulary` rewrites every piece of model output: scenes, polish, repair and outline text.
6. **The lexicon filter flagged nothing** on any final text, and no ladder events fired in runs A and B. The default guardrails did not trip on these prompts, which are built from positively framed beats.

## Sample output

Run B, scenes 1–2, as shipped (polish off). Title: *Star-Glow Sharing*.

> Mila pressed her nose to the cool glass, letting the pale window light spill like honey across her cheeks and the quiet room where dust motes danced in slow circles above her blanket. Outside, the garden fence stood like a sleepy old friend, its wooden posts wrapped in soft green moss that tickled her fingertips when she imagined reaching out. Near the fence, a tiny star blinked—a shimmering dot in the vast sky—while Finn the fox paused, ears twitching at the hush of leaves that whispered secrets to the earth like soft lullabies. […] Finn took a small step forward, his paws padding lightly on the dewy grass, and the world felt full of quiet magic.
>
> Mila and Finn settled onto the mossy stone, its soft green blanket cradling their shoulders as moonlight spilled like silver honey over the garden fence […]. They sat in the hush, surrounded by soft shadows and the hush of falling leaves, and the world felt safe and full of gentle promise.

Poster choice: `StarfieldViolet`, Moon, Hills, `WindowGlow` focal, `StarCluster` accent, mood "cozy". `PosterComposer.Validate` accepted it unchanged.

## Simulator

The iOS 27.0 iPhone 17 simulator reported the model as **available**: it uses the host Mac's model. A full story was generated in the app through the binding in about 2 minutes for a 10-minute (8-scene) length, while the Mac lab was idle.

The fallback path is therefore covered by the Core unit tests (`AppleFoundationStoryEngineTests`), not by the simulator. Those tests cover unavailability, guardrail exhaustion, more than 2 fallen-back scenes, assets, locale, rate limits and context overflow.

## Still unverified

- Speed and memory on an A18/A19 iPhone.
- Guardrail trip rate across many profiles and interests.
- Whether the default guardrails trip less now that beats are reframed positively. The lab saw no trips; the orchestrator lab saw 2–3 of 5 scenes trip before reframing.
- Behaviour while backgrounded.
- How quickly cancellation takes effect on device.
