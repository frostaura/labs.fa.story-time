using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Services;

/// <summary>
/// Pre-renders the three narrator samples into the engine's cache so a ▶ preview ring plays
/// instantly instead of stalling on a cold render (measured ~50 s with the previous bundled voice,
/// seconds on device — long enough that the founder read the button as broken, 2026-07-31).
///
/// <para>Fire-and-forget, lowest possible ceremony: called when a narrator picker becomes
/// visible (onboarding voice step, recipe shelf). Renders sequentially — one TTS context,
/// no point competing with itself — starting with the voice the child already uses, and
/// yields between voices so a real preview tap always wins the race. Every failure is
/// swallowed: warming is an optimization, never a user-visible operation.</para>
/// </summary>
public sealed class VoiceSampleWarmer(ITtsEngine tts)
{
    private Task? _run;
    private readonly Lock _gate = new();

    /// <summary>Starts warming once per app run. Safe to call from every picker open.</summary>
    public void Warm(NarratorVoice preferredFirst)
    {
        lock (_gate)
        {
            if (_run is not null) return;
            _run = Task.Run(() => WarmAllAsync(preferredFirst));
        }
    }

    private async Task WarmAllAsync(NarratorVoice preferredFirst)
    {
        var order = new List<NarratorVoice> { preferredFirst };
        order.AddRange(Enum.GetValues<NarratorVoice>().Where(v => v != preferredFirst));

        foreach (var voice in order)
        {
            try
            {
                await tts.RenderVoiceSampleAsync(voice, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Warming is best-effort — a failure here must never surface anywhere.
            }
            // Breathe: a user tapping a ring mid-warm gets the engine promptly.
            await Task.Delay(250).ConfigureAwait(false);
        }
    }
}
