using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using StoryTime.Core.Engines.AppleFoundation;

namespace FmLab;

/// <summary>IFoundationModelsBridge over the macOS lab host's JSON-lines pipe — the same
/// SEFMEngine Swift code the iOS framework ships, driven from the real C# engine.</summary>
public sealed class PipeBridge : IFoundationModelsBridge, IDisposable
{
    private readonly Process _process;
    private readonly ConcurrentDictionary<int, (TaskCompletionSource<(int Code, string? Result, string? Error)> Tcs, Action<string>? OnDelta)> _pending = new();
    private readonly object _writeGate = new();
    private int _nextId;

    public PipeBridge(string hostPath)
    {
        _process = Process.Start(new ProcessStartInfo(hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("lab host did not start");
        _ = Task.Run(ReadLoopAsync);
    }

    private async Task ReadLoopAsync()
    {
        while (await _process.StandardOutput.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt32();
            if (!_pending.TryGetValue(id, out var entry)) continue;
            if (root.GetProperty("kind").GetString() == "delta")
            {
                entry.OnDelta?.Invoke(root.GetProperty("text").GetString() ?? string.Empty);
                continue;
            }
            _pending.TryRemove(id, out _);
            entry.Tcs.TrySetResult((root.GetProperty("code").GetInt32(),
                root.TryGetProperty("result", out var r) ? r.GetString() : null,
                root.TryGetProperty("error", out var e) ? e.GetString() : null));
        }
    }

    private Task<(int Code, string? Result, string? Error)> Send(string op, string json, Action<string>? onDelta = null) =>
        Send(Interlocked.Increment(ref _nextId), op, json, onDelta);

    private Task<(int Code, string? Result, string? Error)> Send(int id, string op, string json, Action<string>? onDelta)
    {
        var tcs = new TaskCompletionSource<(int, string?, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (tcs, onDelta);
        var line = JsonSerializer.Serialize(new { id, op, json });
        lock (_writeGate)
        {
            _process.StandardInput.WriteLine(line);
            _process.StandardInput.Flush();
        }
        return tcs.Task;
    }

    private async Task<string> Call(string op, string json, CancellationToken ct, Action<string>? onDelta = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var task = Send(id, op, json, onDelta);
        using var reg = ct.Register(() => _ = Send("cancel", id.ToString()));
        var (code, result, error) = await task;
        if (code == 0 && result is not null) return result;
        if (code == (int)FmErrorCode.Cancelled && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
        throw FoundationModelsException.FromNative(code, error);
    }

    public FoundationModelsAvailability GetAvailability() =>
        FoundationModelsAvailability.Parse(Send("availability", "{}").GetAwaiter().GetResult().Result ?? "{}");

    public void Prewarm(string stage) => _ = Send("prewarm", stage);

    public async Task<int?> CountTokensAsync(string text, CancellationToken ct)
    {
        var (code, result, _) = await Send("count", text);
        return code == 0 && int.TryParse(result, out var n) && n >= 0 ? n : null;
    }

    public Task<string> GenerateOutlineAsync(string requestJson, CancellationToken ct) => Call("outline", requestJson, ct);
    public Task<string> StreamSceneAsync(string requestJson, Action<string> onDelta, CancellationToken ct) => Call("scene", requestJson, ct, onDelta);
    public Task<string> DigestSceneAsync(string requestJson, CancellationToken ct) => Call("digest", requestJson, ct);
    public Task<string> PolishSceneAsync(string requestJson, CancellationToken ct) => Call("polish", requestJson, ct);
    public Task<string> GeneratePosterAsync(string requestJson, CancellationToken ct) => Call("poster", requestJson, ct);

    public void Dispose()
    {
        _process.StandardInput.Close();
        if (!_process.WaitForExit(5000)) _process.Kill();
        _process.Dispose();
    }
}
