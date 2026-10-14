using System.Runtime.InteropServices;
using AVFoundation;
using StoryTime.Core.Engines.Narration;

namespace StoryTime.App.Platforms.iOS.Narration;

/// <summary>
/// Renders ONE utterance at a time with <c>AVSpeechSynthesizer.WriteUtterance</c> into PCM for
/// the SoothingNarrator chain. Measured behaviour this class is built around
/// (docs/research/voice-lab.md):
/// <list type="bullet">
/// <item>A zero-length buffer arrives at every paragraph break, NOT only at the end — it is
/// never treated as completion. Completion is the delegate's <c>DidFinishSpeechUtterance</c>,
/// which fires in write mode, followed by a short quiet period so trailing buffers land.</item>
/// <item>Buffers are read by their real processing format (Float32 mono non-interleaved in
/// practice; Int16/Int32 and interleaved layouts are handled, never assumed away). The sample
/// rate is the voice's own (16, 22.05 or 48 kHz); the chain resamples.</item>
/// <item>The synthesizer and its delegate are held in fields for the whole write; writes are
/// serialised by a semaphore. Cancellation (including SoothingNarrator's 4× timeout) stops
/// the synthesizer immediately.</item>
/// <item>Utterance delays are 0 (they are ignored when writing to buffers anyway) and
/// assistive-technology preferences are off, so the plan's rate and pitch are what is heard.</item>
/// </list>
/// </summary>
public sealed class AvSpeechUtteranceRenderer : IUtteranceSynthesizer
{
    private static readonly TimeSpan QuietAfterFinish = TimeSpan.FromMilliseconds(60);

    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly AVSpeechSynthesizer _synthesizer = new();
    private readonly CompletionDelegate _delegate = new();

    public AvSpeechUtteranceRenderer()
    {
        _synthesizer.Delegate = _delegate;
    }

    public async Task<UtterancePcm> RenderAsync(UtteranceRequest request, CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var voice = AVSpeechSynthesisVoice.FromIdentifier(request.VoiceId)
                        ?? throw new VoiceRenderException($"voice {request.VoiceId} is not available");
            using var utterance = new AVSpeechUtterance(request.Text)
            {
                Voice = voice,
                Rate = request.Rate,
                PitchMultiplier = request.Pitch,
                Volume = 1.0f,
                PreUtteranceDelay = 0,
                PostUtteranceDelay = 0,
                PrefersAssistiveTechnologySettings = false,
            };
            var job = new WriteJob(utterance);
            _delegate.Current = job;
            using var registration = ct.Register(() =>
            {
                _synthesizer.StopSpeaking(AVSpeechBoundary.Immediate);
                job.Cancel();
            });
            _synthesizer.WriteUtterance(utterance, job.Append);
            await job.WaitAsync(QuietAfterFinish).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return job.ToPcm();
        }
        finally
        {
            _delegate.Current = null;
            _one.Release();
        }
    }

    /// <summary>Completion signals for the utterance being written (held strongly by the renderer).</summary>
    private sealed class CompletionDelegate : AVSpeechSynthesizerDelegate
    {
        public WriteJob? Current { get; set; }

        public override void DidFinishSpeechUtterance(AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
            => Match(utterance)?.Finish();

        public override void DidCancelSpeechUtterance(AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
            => Match(utterance)?.Cancel();

        private WriteJob? Match(AVSpeechUtterance utterance)
        {
            var job = Current;
            return job is not null && ReferenceEquals(job.Utterance, utterance) ? job : null;
        }
    }

    /// <summary>Collects one utterance's buffers (callbacks may arrive on any thread).</summary>
    private sealed class WriteJob(AVSpeechUtterance utterance)
    {
        private readonly Lock _gate = new();
        private readonly List<float[]> _chunks = [];
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _lastBufferTicks = Environment.TickCount64;
        private int _sampleRate;
        private Exception? _error;

        public AVSpeechUtterance Utterance { get; } = utterance;

        public void Append(AVAudioBuffer buffer)
        {
            try
            {
                if (buffer is not AVAudioPcmBuffer pcm) return;
                var frames = (int)pcm.FrameLength;
                Interlocked.Exchange(ref _lastBufferTicks, Environment.TickCount64);
                if (frames == 0) return;                      // paragraph break — NOT the end
                var format = pcm.Format;
                var samples = ReadMono(pcm, format, frames);
                lock (_gate)
                {
                    var rate = (int)Math.Round(format.SampleRate);
                    if (_sampleRate == 0) _sampleRate = rate;
                    else if (_sampleRate != rate) throw new VoiceRenderException($"sample rate changed mid-utterance ({_sampleRate} → {rate})");
                    _chunks.Add(samples);
                }
            }
            catch (Exception ex)
            {
                _error ??= ex;
                _finished.TrySetResult();
            }
        }

        public void Finish() => _finished.TrySetResult();

        public void Cancel() => _finished.TrySetCanceled();

        public async Task WaitAsync(TimeSpan quiet)
        {
            await _finished.Task.ConfigureAwait(false);
            // Let trailing buffer callbacks drain: wait until none has arrived for `quiet`.
            for (var i = 0; i < 50; i++)
            {
                var since = Environment.TickCount64 - Interlocked.Read(ref _lastBufferTicks);
                if (since >= quiet.TotalMilliseconds) break;
                await Task.Delay(quiet).ConfigureAwait(false);
            }
            if (_error is not null) throw _error as VoiceRenderException ?? new VoiceRenderException("buffer read failed", _error);
        }

        public UtterancePcm ToPcm()
        {
            lock (_gate)
            {
                var total = _chunks.Sum(c => c.Length);
                var all = new float[total];
                var at = 0;
                foreach (var c in _chunks)
                {
                    Array.Copy(c, 0, all, at, c.Length);
                    at += c.Length;
                }
                return new UtterancePcm(all, _sampleRate);
            }
        }

        /// <summary>Channel 0 of the buffer, as float, honouring its real layout.</summary>
        private static float[] ReadMono(AVAudioPcmBuffer pcm, AVAudioFormat format, int frames)
        {
            var channels = (int)Math.Max(1, format.ChannelCount);
            var interleaved = format.Interleaved && channels > 1;
            var count = interleaved ? frames * channels : frames;
            var mono = new float[frames];
            switch (format.CommonFormat)
            {
                case AVAudioCommonFormat.PCMFloat32:
                {
                    var data = new float[count];
                    Marshal.Copy(Marshal.ReadIntPtr(pcm.FloatChannelData), data, 0, count);
                    for (var i = 0; i < frames; i++) mono[i] = data[interleaved ? i * channels : i];
                    break;
                }
                case AVAudioCommonFormat.PCMInt16:
                {
                    var data = new short[count];
                    Marshal.Copy(Marshal.ReadIntPtr(pcm.Int16ChannelData), data, 0, count);
                    for (var i = 0; i < frames; i++) mono[i] = data[interleaved ? i * channels : i] / 32768f;
                    break;
                }
                case AVAudioCommonFormat.PCMInt32:
                {
                    var data = new int[count];
                    Marshal.Copy(Marshal.ReadIntPtr(pcm.Int32ChannelData), data, 0, count);
                    for (var i = 0; i < frames; i++) mono[i] = data[interleaved ? i * channels : i] / 2147483648f;
                    break;
                }
                default:
                    throw new VoiceRenderException($"unsupported speech buffer format {format.CommonFormat}");
            }
            return mono;
        }
    }
}
