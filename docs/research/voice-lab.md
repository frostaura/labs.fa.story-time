# Voice lab: measured results (macOS 27, 2026-10-14)

These are empirical AVSpeechSynthesizer experiments, run on this Mac with small Swift scripts and
ffmpeg. **Where these measurements contradict [`narration.md`](narration.md), they win.** The
samples and scripts were scratch files under `/private/tmp/claude-lab-voice/` and are not kept.

**Gap in the evidence: no premium voices were installed.** The Mac had only compact voices
(Samantha, Karen, Daniel, Moira, Tessa) at 22.05 kHz, plus the Siri voice Nora and the Indian
English voices Tara and Aman at 48 kHz. Nora, Tara and Aman look like Siri-tier voices: an unsigned
binary cannot resolve them, so they are not available to apps. Whatever is ranked below as the
best premium voice still needs to be heard on a device.

## Measured behaviour

**Rendering to a file with `write(_:toBufferCallback:)`:**
- **`pre/postUtteranceDelay` are ignored.** The output is byte-identical with or without them.
  Every pause has to be spliced in as silence by us.
- **A zero-length buffer arrives at every paragraph break, not only at the end of the text.**
  Stopping on the first one cut a 42 s story down to 12 s. Wait for the delegate's `didFinish`
  instead, which does fire in write mode.
- **Output is always Float32, mono, non-interleaved.** The sample rate depends on the voice:
  22.05, 16 or 48 kHz. Resample everything to 48 kHz.
- **Each voice adds 0–250 ms of its own leading and trailing silence.** Trim it (−50 dBFS
  threshold, keep 30 ms, 8 ms fades) so the pause map stays exact.
- **The voice list is not stable.** Samantha's identifier changed between runs, so voices are
  selected at runtime by quality and traits. Filter out novelty voices (15 English ones) and the
  `com.apple.eloquence` voices.

**Utterance parameters:**
- **`rate` has a weak effect below 0.5:** 0.40 is ×1.10 the duration, 0.30 is ×1.21, 0.0 only
  ×1.78.
- **Compact voices only change speed in about 10% steps,** so 0.36–0.44 all sound the same. The
  premium and Siri-class voices are continuous.
- **`pitchMultiplier` barely does anything:** 0.8 lowers pitch by only about 4.5%, and Nora
  ignores it entirely.
- **`volume` works on most voices, but Tara ignores it.** Apply gain in the DSP stage instead.

**SSML in write mode works, but behaves differently per voice:**
- `<break>` adds to the voice's own pause on some voices and replaces it on others. A requested
  650 ms came out as 0.71–1.13 s depending on the voice.
- `<prosody rate>` works on every voice ("slow" ×1.31, "x-slow" ×1.6).
- `<prosody pitch>` and `<prosody volume>` work on some voices only.
- `<emphasis>` has no effect: the output is byte-identical.
- **Decision: no SSML.** Spliced silence landed within about 80 ms of target on every voice.

**Expressiveness (F0 range in semitones):** Daniel 5.2, Nora 4.4, Samantha 3.4, Tara 2.8,
Moira 2.2, Karen 1.8.

**Measured on a 121-word passage:**

| Sample | Duration | wpm | Long pauses | Loudness |
|---|---|---|---|---|
| Baseline (Samantha, rate 0.5, one utterance) | 40.6 s | 179 | none longer than 0.30 s | −16.9 LUFS |
| Sentence-segmented with the pause map below | about 54 s | about 132 | 4 | — |
| With the pad | — | 110–124 | — | — |

## Recipe (measured; supersedes narration.md where they differ)

**Voice selection.** Best premium English voice (prefer Ava, Zoe or Serena), then enhanced, then
Moira or Samantha, with Daniel as the male option. Exclude novelty, eloquence and Siri voices.
Re-validate the persisted identifier on every launch. Show the parent the Settings path when no
premium voice is installed.

**Rate, pitch and volume.**
- Base rate 0.40. Pitch 1.0. Volume 1.0, with all gain applied in DSP.
- Dialogue: rate +0.02, pitch ×1.08 (works on some voices only), −2 dB.
- Final paragraph: rate ramps linearly from 0.40 to 0.28 and gain from 0 to −6 dB. The goodnight
  lines also get pitch ×0.97.

**Segmentation.** One utterance per sentence. A dialogue quote and its tag ("…," it whispered) are
separate pieces, and "…" splits a sentence. **Never split at commas:** voices already pause
150–300 ms there.

**Pause map (spliced silence, after trimming):**

| Boundary | Silence |
|---|---|
| Sentence | 650 ms |
| Before dialogue | 450 ms |
| Dialogue → tag | 250 ms |
| After the tag | 600 ms |
| "…" | 750 ms |
| Paragraph | 1,400 ms |
| Scene change | 2,200 ms (extrapolated) |
| Before the ending | 1,800 ms |
| Between "Goodnight, Mila." and "Goodnight." | 1,100 ms |
| Story head / tail | 600 ms / 1,500 ms |
| Extra head / tail with the pad | +3 s / +4–6 s |

**DSP chain (the ffmpeg chain that was measured):**
```
aresample=48000, highpass=70, eq 180Hz +2.5dB (w0.9), eq 3.2kHz −1.5dB (w1.0), de-ess,
highshelf 7kHz −3dB, lowpass 12kHz, compressor −26dB 2.5:1 att 12ms rel 180ms knee 6 makeup 2
```
- Small-room reverb: 0.35 s decay, 10 ms pre-delay, wet band 200 Hz–5 kHz, sent 17 dB below the
  dry signal.
- Normalise to **−18 LUFS**, peak −2 dB.
- On Nora this cut energy above 7 kHz from −15.7 to −22.3 dB and the sibilance band from −14.8 to
  −19.9 dB.

**Pad.** A slow Fmaj7 → Cadd9 → Am7 → Dm loop:
- 10 s per chord with 4 s crossfades.
- Low-passed at 900 Hz, with a slight stereo detune.
- Sits **28 LU below** the voice.
- Fades in over 3 s and out over 5 s.
- The final mix is normalised to −18 LUFS.

**Personal Voice.** Authorization was denied on the Mac and no voice was listed; not pursued for
v1.
