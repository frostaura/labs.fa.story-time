# Soothing narration with Apple speech (research, 2026-10-14)

The founder heard plain `AVSpeechSynthesizer` output and called it "extremely robotic". Voice
quality is a top acceptance bar. This file holds the desk research. Measured results from the
voice lab go in [`voice-lab.md`](voice-lab.md) and override these numbers where they disagree.
**[judgement]** means a number with no source behind it, to be tuned by ear on device.

## What constrains the design

1. **Apps cannot download or deep-link to better voices.**
   - `speechVoices()` lists installed voices only, and there is no download API.
   - `App-Prefs:` links are private and dead since iOS 18.
   - Recent iOS reportedly ships only Samantha by default, so most families start on a compact
     voice. That is almost certainly the robotic voice the founder heard.
   - **Parent onboarding that gets a premium voice downloaded is the biggest quality lever.**
2. **Siri voices are not available to apps.** If one is selected, the API falls back
   (WWDC20-10022).
3. **Personal Voice cannot be rendered to a buffer.** The OS logs "Cannot use
   AVSpeechSynthesizerBufferCallback with Personal Voices". It can only play live, so it gets no
   mastering and no music bed. It is not in v1.
4. **SSML disables `rate`/`pitchMultiplier`**, Apple publishes no list of supported tags, and the
   tags are buggy in places (`<sub>` and `<phoneme>` add pauses, `<emphasis>` froze an iOS 18
   beta, `contour` is ignored). **Use no SSML in the main path; splice silence ourselves.**
5. **iOS 26 regression (FB20271264):** `AVSpeechSynthesisVoice(language:)` ignores the voice chosen
   in Settings. Always select a voice by identifier from `speechVoices()`.

## Voices (quality tier per locale; third-party list, verify on device)

| Locale | Enhanced | Premium |
|---|---|---|
| en-US | Allison, Ava, Evan, Joelle, Nathan, Noelle, Samantha, Susan, Tom, Zoe | Ava |
| en-GB | Daniel, Jamie, Kate, Oliver, Serena, Stephanie | Jamie, Serena |
| en-AU | Karen, Lee, Matilda | Karen, Matilda |
| en-ZA | Tessa | none |
| en-IE | Moira | none |

## SoothingNarrator recipe

### P1 · Voice selection

1. Build candidates from `speechVoices()`: English, not novelty, not Personal Voice.
2. Score each candidate:
   - **Tier:** premium 300, enhanced 200, default 0.
   - **Warmth bonus [judgement]:** Ava 40, Zoe 35, Serena 35, Matilda 30, Jamie 25, Evan 25,
     Nathan 20, Karen 20, Kate 20, Stephanie 15, Tessa 15, Moira 15.
   - **Region match:** +50 when the voice's region matches the device region.
3. Persist the chosen identifier. Re-run selection on `availableVoicesDidChangeNotification`.
4. **If the best voice is default quality,** show the parent a "Download a softer storyteller
   voice" card. It plays a short pre-rendered premium sample (a bundled asset) and gives the steps
   Settings › Accessibility › Spoken Content › Voices › English › "Ava (Premium)". The only link it
   offers is `UIApplication.OpenSettingsUrlString`.

### P2 · Segmentation and text normalisation

- Write numbers and abbreviations out in words.
- Turn `—` and `;` into `,`.
- Turn `…` into `.` and add 400 ms.
- Turn `!` into `.` in narration, but keep it in dialogue.
- Split sentences longer than 22 words at a comma or conjunction.
- Render **one utterance per sentence or clause**. The child's name gets an IPA attributed range
  when a pronunciation is stored.

### P3 · Utterance parameters

- `pitchMultiplier` 0.97, `volume` 1.0, `prefersAssistiveTechnologySettings` false, utterance
  delays 0.
- `rate` is calibrated per voice to **105 wpm** for ages 3–5 and **115 wpm** for ages 6–8:
  1. Render a 60-word passage and measure its trimmed duration.
  2. Binary-search the rate between 0.30 and 0.50.
  3. Cache the result per voice identifier.
- Children process speech at around 124 wpm; audiobooks run 150–160.

### P4 · Pause map (spliced silence) [judgement]

| Boundary | Pause |
|---|---|
| Clause | 250 ms |
| Sentence | 650 ms |
| Paragraph | 1.3 s |
| After a line of dialogue | 800 ms |
| Scene change | 2.8 s, with the music bed swelling |
| Before the final line | 2.0 s |

Add ±10% jitter, keeping the total length unchanged. Trim each segment's own silence to 30 ms and
apply 10 ms edge fades.

### P5 · Dialogue [judgement]

Use the same voice as its own utterance:

- Small or young characters: pitch 1.04, rate +3%.
- Large or old characters: pitch 0.93, rate −4%.

Keep pitch within ±8%.

### P6 · Ending [judgement]

- Over the last 15% of sentences, ramp the rate linearly down to 92% and the pauses up to ×1.4.
- Lower the final paragraph by −3 dB.
- Hold 3 s of music after the last word, then fade the music out over 25 s.

### P7 · Mastering (offline, 48 kHz) [judgement]

| Stage | Setting |
|---|---|
| High-pass | 80 Hz, 12 dB/oct |
| Low shelf | +2 dB at 180 Hz |
| Bell | −2.5 dB at 3.2 kHz, Q 1.0 |
| High shelf | −3 dB from 7 kHz |
| Compressor | −22 dBFS, about 2:1, attack 15 ms, release 200 ms, at most 3 dB of gain reduction |
| Reverb | small room, 8–10% wet |
| Output target | **−20 LUFS** integrated, true peak ≤ −3 dBTP (BS.1770 meter written in-house) |

The existing cozy-mastering chain in `Core/Engines/Narration/Prosody.cs` is the starting point.

### P8 · Music bed [judgement, above the WCAG 20 dB floor]

- Under speech: −22 dB relative to the voice.
- In scene gaps: −14 dB, with a 1.5 s attack, ducking back 0.8 s before the voice resumes.
- Fade in over 4 s, with 3 s of music before the first word.
- End on a 25–30 s music tail.
- Never compress the final mix.

## Rendering pitfalls (write mode)

- **Buffer format.** Buffers are typically 22,050 Hz mono, and the declared `commonFormat` can be
  wrong. The safe path: write the buffers to a temporary CAF using `pcm.format.settings`, read it
  back via `processingFormat`, then resample to 48 kHz.
- **End of stream.** A buffer with `frameLength == 0` means the voice has finished.
- **Lifetime.** Keep the synthesizer and the callback delegate strongly referenced for the whole
  write.
- **Write discipline.**
  - One write at a time.
  - Track each write with a `TaskCompletionSource`.
  - Time out at 4× the expected duration.
  - Check segment RMS is above −60 dBFS; otherwise fall back to the next voice.
- **Long utterances can stop early** ("Daniel Enhanced" on iOS 17), which per-sentence rendering
  avoids.
- **Playback session.** Use `.playback` with `UIBackgroundModes=audio`.
