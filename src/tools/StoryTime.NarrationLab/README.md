# Narration lab (macOS)

Renders `story.txt` (a ~2-minute, three-scene story with dialogue and a goodnight ending)
through the **real** SoothingNarrator code with macOS `AVSpeechSynthesizer` voices, the same
engine iOS uses, and then measures the result.

```sh
src/tools/StoryTime.NarrationLab/run-lab.sh            # Samantha, Moira, Daniel (+ Serena if installed)
src/tools/StoryTime.NarrationLab/run-lab.sh Serena     # any installed English voice by name
OUT=/some/dir STORY=other.txt src/tools/StoryTime.NarrationLab/run-lab.sh
```

Steps:

1. `plan`: `SoothingPlanner` turns the story into utterances (`plan.json`, plus a readable
   `plan.plan.txt`).
2. `render-utterances.swift`: one `write(_:toBufferCallback:)` per utterance. It waits for
   `didFinish`, because zero-length buffers also arrive at paragraph breaks. It writes
   `NNNN.f32` and `NNNN.json` files.
3. `mix`: `SoothingNarrator` runs over those files, so trimming, resampling, mastering,
   loudness and the pad are the app's own code. It writes the `.wav`, the voice and pad
   stems, and a `.report.json`.
4. `ffmpeg` encodes an `.m4a` and cross-checks loudness with `ebur128`.

The report gives:

- duration and wpm;
- integrated LUFS and true peak, using the BS.1770 mono convention (one channel, G = 1, the
  same as ffmpeg);
- the pad's offset in LU below the voice;
- compressor, limiter and de-esser maxima;
- per-pause planned vs. measured silence at −45 dBFS (perceptual) and −50 dBFS (strict);
- mix throughput as a real-time factor.

The baseline is the plain voice: Samantha, one utterance, default rate, no processing.

Premium voices such as Serena only appear once you download them on this Mac (System Settings
› Accessibility › Spoken Content › System voice › Manage Voices).
