using CoreFoundation;
using Network;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Platforms.iOS.Narration;

/// <summary>
/// How the device is connected, from the OS path monitor (Network framework). It only reads
/// the system's view of the current path — it never opens a connection — and exists solely to
/// word the storyteller-voice download notice (mobile data vs Wi-Fi). An expensive path
/// (cellular, or a personal hotspot) counts as mobile data.
/// </summary>
public sealed class NetworkPathStatus : INetworkStatus, IDisposable
{
    private readonly NWPathMonitor _monitor = new();
    private volatile int _current = (int)NetworkKind.Unknown;

    public NetworkPathStatus()
    {
        _monitor.SnapshotHandler = path =>
        {
            var next = (int)Classify(path);
            if (Interlocked.Exchange(ref _current, next) != next) Changed?.Invoke(this, EventArgs.Empty);
        };
        _monitor.SetQueue(new DispatchQueue("net.frostaura.storytime.path-status"));
        _monitor.Start();
    }

    public NetworkKind Current => (NetworkKind)_current;

    public event EventHandler? Changed;

    private static NetworkKind Classify(NWPath path)
    {
        if (path.Status != NWPathStatus.Satisfied) return NetworkKind.Offline;
        if (path.IsExpensive || path.UsesInterfaceType(NWInterfaceType.Cellular)) return NetworkKind.Cellular;
        if (path.UsesInterfaceType(NWInterfaceType.Wifi) || path.UsesInterfaceType(NWInterfaceType.Wired)) return NetworkKind.WiFi;
        return NetworkKind.Unknown;
    }

    public void Dispose() => _monitor.Cancel();
}
