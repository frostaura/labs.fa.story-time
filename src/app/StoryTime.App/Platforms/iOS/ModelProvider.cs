using System.Reflection;
using Foundation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using StoryTime.Core.Abstractions;

namespace StoryTime.App.Platforms.iOS;

/// <summary>
/// iOS model resolution (architecture §11). Order:
///   1. AppData/models (Debug bootstrap materialized here)
///   2. NSBundle resource path (Release MauiAsset bundling — read in place, no copy)
///   3. #if DEBUG only: LAN bootstrap from scripts/serve-models.sh (default host is the
///      Mac's Bonjour name baked at build time by Native.props → "&lt;mac&gt;.local:8787",
///      so simulator AND physical iPhone self-fetch; AppData/model-server.txt overrides).
/// </summary>
public sealed class IosModelProvider : IModelProvider
{
    private const string LlmFileName = "Qwen3-1.7B-Q4_K_M.gguf";
    private const string TtsDirName = "kokoro-multi-lang-v1_0";

    private readonly ILogger<IosModelProvider> _log;

    public IosModelProvider(ILogger<IosModelProvider>? log = null)
        => _log = log ?? NullLogger<IosModelProvider>.Instance;

    /// <summary>Bytes-progress hook for the Weaving screen: (fileName, bytesSoFar, totalBytes
    /// or -1 when the server sent no length). Set by UI code; downloads also log every ~64 MB.</summary>
    public static Action<string, long, long>? DownloadProgress;

    private static string ModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "models");

    public async Task<ModelAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsRoot);
        var llmPath = Path.Combine(ModelsRoot, LlmFileName);
        var ttsDir = Path.Combine(ModelsRoot, TtsDirName);

        // Release path: models bundled as MauiAssets are read straight from the bundle (mmap-safe).
        if (!File.Exists(llmPath))
        {
            var bundled = NSBundle.MainBundle.PathForResource(Path.GetFileNameWithoutExtension(LlmFileName), "gguf");
            if (bundled is not null) llmPath = bundled;
        }
        if (!TtsComplete(ttsDir))
        {
            var bundledDir = Path.Combine(NSBundle.MainBundle.ResourcePath ?? string.Empty, TtsDirName);
            if (TtsComplete(bundledDir)) ttsDir = bundledDir;
        }

#if DEBUG
        if (!File.Exists(llmPath) || !TtsComplete(ttsDir))
        {
            try
            {
                llmPath = Path.Combine(ModelsRoot, LlmFileName);
                ttsDir = Path.Combine(ModelsRoot, TtsDirName);
                await DebugLanBootstrapAsync(llmPath, ttsDir, ct);
            }
            catch (Exception ex)
            {
                // Best-effort: no LAN server → template/platform fallbacks run.
                _log.LogWarning("Debug model bootstrap unavailable ({Message}) — falling back to template/platform engines", ex.Message);
            }
        }
#else
        await Task.CompletedTask;
#endif

        return new ModelAvailability
        {
            LlmReady = File.Exists(llmPath),
            LlmPath = File.Exists(llmPath) ? llmPath : null,
            TtsReady = TtsComplete(ttsDir),
            TtsDirectory = TtsComplete(ttsDir) ? ttsDir : null,
        };
    }

    private static bool TtsComplete(string dir) =>
        (File.Exists(Path.Combine(dir, "model.onnx")) || File.Exists(Path.Combine(dir, "model.int8.onnx")))
        && File.Exists(Path.Combine(dir, "voices.bin"))
        && File.Exists(Path.Combine(dir, "tokens.txt"))
        && Directory.Exists(Path.Combine(dir, "espeak-ng-data"));

#if DEBUG
    /// <summary>Default bootstrap host: build-time-baked "&lt;mac-LocalHostName&gt;.local:8787"
    /// (Native.props _StoryTimeBakeModelServerHost), overridable via AppData/model-server.txt.</summary>
    internal static string DefaultHost()
    {
        var baked = typeof(IosModelProvider).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "StoryTime.ModelServerDefault")?.Value;
        return string.IsNullOrWhiteSpace(baked) ? "localhost:8787" : baked;
    }

    /// <summary>Sole sanctioned network use (Debug model bootstrap, CLAUDE.md invariant).</summary>
    private async Task DebugLanBootstrapAsync(string llmPath, string ttsDir, CancellationToken ct)
    {
        var hostFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "model-server.txt");
        var host = File.Exists(hostFile) ? File.ReadAllText(hostFile).Trim() : DefaultHost();
        _log.LogInformation("Debug model bootstrap: http://{Host}/ → {Root}", host, ModelsRoot);
        using var http = new HttpClient { BaseAddress = new Uri($"http://{host}/"), Timeout = TimeSpan.FromMinutes(30) };

        if (!File.Exists(llmPath))
            await DownloadAsync(http, LlmFileName, llmPath, ct);

        if (!TtsComplete(ttsDir))
        {
            var tarPath = ttsDir + ".tar";
            await DownloadAsync(http, TtsDirName + ".bundle.tar", tarPath, ct);
            if (Directory.Exists(ttsDir)) Directory.Delete(ttsDir, true);
            await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(tarPath, Path.GetDirectoryName(ttsDir)!, true, ct);
            File.Delete(tarPath);
        }
    }

    private async Task DownloadAsync(HttpClient http, string remoteName, string destPath, CancellationToken ct)
    {
        var tmp = destPath + ".part";
        using var response = await http.GetAsync(remoteName, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? -1;
        _log.LogInformation("Downloading {Name} ({Total})", remoteName, total < 0 ? "unknown size" : $"{total / (1024.0 * 1024):F0} MB");

        const int logEvery = 64 * 1024 * 1024;
        var buffer = new byte[1 << 16];
        long done = 0, nextLog = logEvery;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(tmp))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                DownloadProgress?.Invoke(remoteName, done, total);
                if (done >= nextLog)
                {
                    _log.LogInformation("  {Name}: {Done:F0} / {Total} MB", remoteName, done / (1024.0 * 1024),
                        total < 0 ? "?" : $"{total / (1024.0 * 1024):F0}");
                    nextLog += logEvery;
                }
            }
        }
        File.Move(tmp, destPath, true);
        _log.LogInformation("Downloaded {Name}: {Done:F0} MB", remoteName, done / (1024.0 * 1024));
    }
#endif
}
