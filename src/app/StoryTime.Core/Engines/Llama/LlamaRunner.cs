using System.Diagnostics;
using System.Text;

namespace StoryTime.Core.Engines.Llama;

/// <summary>Options for one generation call.</summary>
internal sealed class LlamaGenerateOptions
{
    public float Temperature { get; init; } = 0.8f;
    public int MaxTokens { get; init; } = 1024;
    /// <summary>GBNF grammar text; null = free text.</summary>
    public string? Grammar { get; init; }
    public uint Seed { get; init; } = 0; // 0 → time-derived
    public IProgress<int>? Progress { get; init; }
    /// <summary>Reuse the longest common token prefix already resident in the KV cache
    /// from the previous call (llama_memory_seq_rm of the divergent tail; only the new
    /// suffix is decoded). False → full llama_memory_clear, byte-identical to the
    /// pre-cache behavior. Sampling is unaffected either way: the sampler chain is built
    /// fresh per call and only ever accepts generated tokens.</summary>
    public bool ReuseKvPrefix { get; init; } = true;
}

internal sealed class LlamaGenerateResult
{
    public string Text { get; init; } = string.Empty;
    public int PromptTokens { get; init; }
    /// <summary>Prompt tokens served from the KV prefix cache (not re-ingested).</summary>
    public int CachedPromptTokens { get; init; }
    public int GeneratedTokens { get; init; }
    public double PromptTokensPerSecond { get; init; }
    public double GenerateTokensPerSecond { get; init; }
}

/// <summary>
/// Owns one llama.cpp model + context (mmap'd, context 8192, threads min(4, cores))
/// and runs synchronous single-sequence generation. Thread-safe via a single gate —
/// llama contexts are not concurrency-safe and the pipeline is sequential by design.
/// </summary>
internal sealed class LlamaRunner : IDisposable
{
    private readonly object _gate = new();
    private IntPtr _model;
    private IntPtr _ctx;
    private IntPtr _vocab;
    private bool _disposed;
    private static bool _backendInit;

    /// <summary>Tokens currently resident in the KV cache for seq 0 — the previous call's
    /// prompt plus every generated (decoded) token, in position order. Cleared on any
    /// failed/cancelled call so a partial decode can never be mistaken for cache state.</summary>
    private int[] _kvTokens = [];

    public int ContextSize { get; }

    public LlamaRunner(string modelPath, int contextSize = 8192, int? gpuLayers = null, int? threads = null)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("GGUF model not found", modelPath);
        lock (_gate)
        {
            if (!_backendInit) { LlamaNative.BackendInit(); _backendInit = true; }

            var mp = LlamaNative.ModelDefaultParams();
            mp.LoadMode = LlamaNative.LoadModeMmap;
            // Desktop (Metal dylib): offload everything. Android CPU build ignores this.
            mp.NGpuLayers = gpuLayers ?? 99;
            _model = LlamaNative.ModelLoadFromFile(modelPath, mp);
            if (_model == IntPtr.Zero) throw new InvalidOperationException($"llama: failed to load model at {modelPath}");
            _vocab = LlamaNative.ModelGetVocab(_model);

            var nThreads = threads ?? Math.Min(4, Environment.ProcessorCount);
            var cp = LlamaNative.ContextDefaultParams();
            cp.NCtx = (uint)contextSize;
            cp.NBatch = 512;
            cp.NThreads = nThreads;
            cp.NThreadsBatch = nThreads;
            _ctx = LlamaNative.InitFromModel(_model, cp);
            if (_ctx == IntPtr.Zero)
            {
                LlamaNative.ModelFree(_model); _model = IntPtr.Zero;
                throw new InvalidOperationException("llama: failed to create context");
            }
            ContextSize = (int)LlamaNative.NCtx(_ctx);
        }
    }

    /// <summary>Run one prompt → completion. Reuses the KV-cached common token prefix from
    /// the previous call when <see cref="LlamaGenerateOptions.ReuseKvPrefix"/> (default);
    /// otherwise clears KV memory first (fresh sequence per call).</summary>
    public LlamaGenerateResult Generate(string prompt, LlamaGenerateOptions options, CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var promptBytes = Encoding.UTF8.GetBytes(prompt);
            int[] tokens;
            unsafe
            {
                var max = promptBytes.Length + 64;
                var buf = new int[max];
                int n;
                fixed (byte* pText = promptBytes)
                fixed (int* pTok = buf)
                {
                    n = LlamaNative.Tokenize(_vocab, pText, promptBytes.Length, pTok, max, true, true);
                }
                if (n < 0) throw new InvalidOperationException($"llama: tokenize failed ({n})");
                tokens = buf[..n];
            }
            if (tokens.Length >= ContextSize - options.MaxTokens)
                throw new InvalidOperationException($"llama: prompt too long ({tokens.Length} tokens for ctx {ContextSize})");

            // ---- KV prefix reuse ----
            // Longest common token prefix with what the previous call left in the KV cache
            // (its prompt + generated tokens). We always re-decode at least the final prompt
            // token so the sampler has fresh logits at the last position. On any reuse,
            // llama_memory_seq_rm drops the divergent tail [lcp, ∞) of seq 0; positions of
            // the suffix batches then continue from lcp automatically (llama_batch_get_one
            // infers positions from the memory's seq pos_max). If partial removal is
            // unsupported (should not happen for the standard KV cache), fall back to a
            // full clear — correctness first.
            var lcp = 0;
            if (options.ReuseKvPrefix && _kvTokens.Length > 0)
            {
                var limit = Math.Min(_kvTokens.Length, tokens.Length - 1);
                while (lcp < limit && _kvTokens[lcp] == tokens[lcp]) lcp++;
            }
            _kvTokens = []; // invalidated until this call completes successfully
            if (lcp > 0)
            {
                if (!LlamaNative.MemorySeqRm(LlamaNative.GetMemory(_ctx), 0, lcp, -1))
                {
                    LlamaNative.MemoryClear(LlamaNative.GetMemory(_ctx), true);
                    lcp = 0;
                }
            }
            else
            {
                LlamaNative.MemoryClear(LlamaNative.GetMemory(_ctx), true);
            }

            var chain = BuildChain(options);
            try
            {
                var sw = Stopwatch.StartNew();
                // ---- prompt ingestion (suffix only, chunked to n_batch) ----
                unsafe
                {
                    const int batchSize = 512;
                    fixed (int* pTok = tokens)
                    {
                        for (var off = lcp; off < tokens.Length; off += batchSize)
                        {
                            ct.ThrowIfCancellationRequested();
                            var count = Math.Min(batchSize, tokens.Length - off);
                            var batch = LlamaNative.BatchGetOne(pTok + off, count);
                            if (LlamaNative.Decode(_ctx, batch) != 0)
                                throw new InvalidOperationException("llama: decode failed during prompt ingestion");
                        }
                    }
                }
                LlamaNative.Synchronize(_ctx); // Metal decodes asynchronously; sync for honest timing
                var promptSeconds = sw.Elapsed.TotalSeconds;
                sw.Restart();

                // ---- token loop ----
                var outBytes = new List<byte>(options.MaxTokens * 4);
                var generatedTokens = new List<int>(options.MaxTokens);
                var generated = 0;
                var pieceBuf = new byte[256];
                while (generated < options.MaxTokens)
                {
                    ct.ThrowIfCancellationRequested();
                    var tok = LlamaNative.SamplerSample(chain, _ctx, -1);
                    if (LlamaNative.VocabIsEog(_vocab, tok)) break;

                    unsafe
                    {
                        fixed (byte* pBuf = pieceBuf)
                        {
                            var len = LlamaNative.TokenToPiece(_vocab, tok, pBuf, pieceBuf.Length, 0, false);
                            for (var i = 0; i < len; i++) outBytes.Add(pieceBuf[i]);
                        }
                    }
                    generated++;
                    if (generated % 8 == 0) options.Progress?.Report(generated);

                    unsafe
                    {
                        var next = tok;
                        var batch = LlamaNative.BatchGetOne(&next, 1);
                        if (LlamaNative.Decode(_ctx, batch) != 0)
                            throw new InvalidOperationException("llama: decode failed during generation");
                    }
                    generatedTokens.Add(tok); // decoded → resident in KV
                }
                options.Progress?.Report(generated);
                var genSeconds = sw.Elapsed.TotalSeconds;

                // Successful call: record exactly what now sits in the KV cache for reuse.
                var kv = new int[tokens.Length + generatedTokens.Count];
                tokens.CopyTo(kv, 0);
                generatedTokens.CopyTo(kv, tokens.Length);
                _kvTokens = kv;

                var ingested = tokens.Length - lcp;
                return new LlamaGenerateResult
                {
                    Text = Encoding.UTF8.GetString(outBytes.ToArray()),
                    PromptTokens = tokens.Length,
                    CachedPromptTokens = lcp,
                    GeneratedTokens = generated,
                    PromptTokensPerSecond = promptSeconds > 0 ? ingested / promptSeconds : 0,
                    GenerateTokensPerSecond = genSeconds > 0 ? generated / genSeconds : 0,
                };
            }
            finally
            {
                LlamaNative.SamplerFree(chain);
            }
        }
    }

    private IntPtr BuildChain(LlamaGenerateOptions o)
    {
        var chain = LlamaNative.SamplerChainInit(LlamaNative.SamplerChainDefaultParams());
        if (o.Grammar is not null)
            LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitGrammar(_vocab, o.Grammar, "root"));
        // repeat penalty on (contract): last 64 tokens, 1.1
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitPenalties(64, 1.1f, 0f, 0f));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTopK(40));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTopP(0.95f, 1));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitMinP(0.05f, 1));
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitTemp(o.Temperature));
        var seed = o.Seed != 0 ? o.Seed : (uint)Environment.TickCount64;
        LlamaNative.SamplerChainAdd(chain, LlamaNative.SamplerInitDist(seed));
        return chain;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_ctx != IntPtr.Zero) { LlamaNative.Free(_ctx); _ctx = IntPtr.Zero; }
            if (_model != IntPtr.Zero) { LlamaNative.ModelFree(_model); _model = IntPtr.Zero; }
        }
    }
}
