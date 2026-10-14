using StoryTime.Core.Abstractions;

namespace StoryTime.App.Platforms.Android;

/// <summary>
/// Android model resolution (architecture §11). Order:
///   1. AppData/models (already materialized — Debug bootstrap or asset-pack copy-once)
///   2. Release asset packs: install-time (TTS) + fast-follow (LLM) copied once to FilesDir
///      for mmap — NOT yet implemented (store-track work; see docs/known-gaps.md)
///   3. #if DEBUG only: LAN bootstrap from scripts/serve-models.sh (http://&lt;mac&gt;:8787)
/// </summary>
public sealed class AndroidModelProvider : IModelProvider
{
    private const string LlmFileName = "Qwen3-1.7B-Q4_K_M.gguf";
    private const string TtsDirName = "kokoro-multi-lang-v1_0";

    private static string ModelsRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "models");

    public async Task<ModelAvailability> GetAvailabilityAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ModelsRoot);
        var llmPath = Path.Combine(ModelsRoot, LlmFileName);
        var ttsDir = Path.Combine(ModelsRoot, TtsDirName);

#if DEBUG
        if (!File.Exists(llmPath) || !TtsComplete(ttsDir))
        {
            try
            {
                await DebugLanBootstrapAsync(llmPath, ttsDir, ct);
            }
            catch (Exception)
            {
                // Bootstrap is best-effort: no server on the LAN → template/platform fallbacks run.
            }
        }
#else
        await Task.CompletedTask; // Release: asset-pack copy-once lands here (known gap)
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
    /// <summary>Sole sanctioned network use (Debug model bootstrap, CLAUDE.md invariant).
    /// Emulator reaches the host at 10.0.2.2; a physical device needs the Mac's LAN IP
    /// written to AppData/model-server.txt (e.g. "192.168.1.20:8787").</summary>
    private static async Task DebugLanBootstrapAsync(string llmPath, string ttsDir, CancellationToken ct)
    {
        var hostFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "model-server.txt");
        var host = File.Exists(hostFile) ? File.ReadAllText(hostFile).Trim() : "10.0.2.2:8787";
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

    private static async Task DownloadAsync(HttpClient http, string remoteName, string destPath, CancellationToken ct)
    {
        var tmp = destPath + ".part";
        await using (var response = await http.GetStreamAsync(remoteName, ct))
        await using (var file = File.Create(tmp))
        {
            await response.CopyToAsync(file, ct);
        }
        File.Move(tmp, destPath, true);
    }
#endif
}
