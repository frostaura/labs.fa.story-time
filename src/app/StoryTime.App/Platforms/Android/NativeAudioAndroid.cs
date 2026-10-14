using Android.Content;
using Android.Media;

namespace StoryTime.App.Platforms.Android;

/// <summary>Android side of the native-player integration (founder request 2026-07-31).
///
/// <para>Most of it needs no code: CommunityToolkit's MediaElement plays through ExoPlayer
/// behind a <c>MediaSession</c>, and <c>MauiProgram</c> already enables the toolkit's media
/// foreground service — so once <c>MetadataTitle</c> / <c>MetadataArtist</c> /
/// <c>MetadataArtworkUrl</c> are set (they now are, in MediaElementNarrationPlayer), the story
/// appears with artwork and working transport controls on Bluetooth, Android Auto, Cast and
/// the lock screen. <c>adb shell dumpsys media_session</c> is the proof.</para>
///
/// <para>The one thing not covered is the "becoming noisy" convention: when headphones are
/// unplugged or a Bluetooth speaker drops, Android does NOT pause for you — it happily keeps
/// playing out of the phone's speaker. For a bedtime story beside a sleeping child that is the
/// worst possible default, so this watcher pauses narration on that broadcast. A weave in
/// flight is untouched; the loom keeps humming.</para></summary>
internal static class NativeAudioAndroid
{
    private static readonly object Gate = new();
    private static BecomingNoisyReceiver? _receiver;
    private static Action? _onNoisy;

    internal static void EnsureBecomingNoisyWatcher(Action onNoisy)
    {
        lock (Gate)
        {
            _onNoisy = onNoisy;
            if (_receiver is not null) return;
            try
            {
                _receiver = new BecomingNoisyReceiver();
                var filter = new IntentFilter(AudioManager.ActionAudioBecomingNoisy);
                global::Android.App.Application.Context.RegisterReceiver(
                    _receiver, filter, ReceiverFlags.NotExported);
            }
            catch (Exception)
            {
                // Registration is best-effort — never take narration down over it.
                _receiver = null;
            }
        }
    }

    private sealed class BecomingNoisyReceiver : BroadcastReceiver
    {
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != AudioManager.ActionAudioBecomingNoisy) return;
            var handler = _onNoisy;
            if (handler is null) return;
            MainThread.BeginInvokeOnMainThread(handler);
        }
    }
}
