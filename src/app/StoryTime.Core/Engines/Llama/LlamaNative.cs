using System.Runtime.InteropServices;

namespace StoryTime.Core.Engines.Llama;

/// <summary>
/// P/Invoke surface over llama.cpp, pinned to release tag <c>b10189</c>
/// (commit b2f221684fcd898e947a121baeda80f345da3e6b). Header snapshot: <c>vendor/llama.h</c>.
/// Struct layouts below mirror that exact tag — upgrading llama.cpp REQUIRES re-verifying
/// every struct against the new header (llama.cpp API churns; see memory/watch.md).
///
/// Library resolution:
///  - macOS harness: libllama.dylib next to the executable (Native.props copies from $(STORYTIME_NATIVE_ROOT)/osx-arm64).
///  - Android: libllama.so packaged in the APK (DllImport("llama") → liblama.so lookup).
///  - iOS: static linking; the App head registers a DllImportResolver that returns
///    NativeLibrary.GetMainProgramHandle() for "llama" (see Platforms/iOS/LlamaNativeResolver.cs).
/// </summary>
internal static partial class LlamaNative
{
    private const string Lib = "llama";

    // ---------- structs (blittable; layout pinned to tag b10189) ----------

    /// <summary>Mirror of <c>struct llama_model_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelParams
    {
        public IntPtr Devices;              // ggml_backend_dev_t *
        public IntPtr TensorBuftOverrides;  // const llama_model_tensor_buft_override *
        public int NGpuLayers;
        public int SplitMode;               // enum llama_split_mode
        public int LoadMode;                // enum llama_load_mode (1 = mmap)
        public int MainGpu;
        public IntPtr TensorSplit;          // const float *
        public IntPtr ProgressCallback;
        public IntPtr ProgressCallbackUserData;
        public IntPtr KvOverrides;
        public byte VocabOnly;
        public byte CheckTensors;
        public byte UseExtraBufts;
        public byte NoHost;
        public byte NoAlloc;
    }

    /// <summary>Mirror of <c>struct llama_context_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ContextParams
    {
        public uint NCtx;
        public uint NBatch;
        public uint NUbatch;
        public uint NSeqMax;
        public uint NRsSeq;
        public uint NOutputsMax;
        public int NThreads;
        public int NThreadsBatch;
        public int CtxType;            // enum llama_context_type
        public int RopeScalingType;
        public int PoolingType;
        public int AttentionType;
        public int FlashAttnType;
        public float RopeFreqBase;
        public float RopeFreqScale;
        public float YarnExtFactor;
        public float YarnAttnFactor;
        public float YarnBetaFast;
        public float YarnBetaSlow;
        public uint YarnOrigCtx;
        public float DefragThold;
        public IntPtr CbEval;
        public IntPtr CbEvalUserData;
        public int TypeK;              // enum ggml_type
        public int TypeV;              // enum ggml_type
        public IntPtr AbortCallback;
        public IntPtr AbortCallbackData;
        public byte Embeddings;
        public byte OffloadKqv;
        public byte NoPerf;
        public byte OpOffload;
        public byte SwaFull;
        public byte KvUnified;
        public IntPtr Samplers;        // struct llama_sampler_seq_config *
        public nuint NSamplers;
        public IntPtr CtxOther;
    }

    /// <summary>Mirror of <c>struct llama_sampler_chain_params</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SamplerChainParams
    {
        public byte NoPerf;
    }

    /// <summary>Mirror of <c>struct llama_batch</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Batch
    {
        public int NTokens;
        public IntPtr Token;    // llama_token *
        public IntPtr Embd;     // float *
        public IntPtr Pos;      // llama_pos *
        public IntPtr NSeqId;   // int32_t *
        public IntPtr SeqId;    // llama_seq_id **
        public IntPtr Logits;   // int8_t *
    }

    internal const int LoadModeMmap = 1; // LLAMA_LOAD_MODE_MMAP

    // ---------- backend ----------

    [DllImport(Lib, EntryPoint = "llama_backend_init")]
    internal static extern void BackendInit();

    [DllImport(Lib, EntryPoint = "llama_backend_free")]
    internal static extern void BackendFree();

    // ---------- model ----------

    [DllImport(Lib, EntryPoint = "llama_model_default_params")]
    internal static extern ModelParams ModelDefaultParams();

    [DllImport(Lib, EntryPoint = "llama_model_load_from_file")]
    internal static extern IntPtr ModelLoadFromFile([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ModelParams p);

    [DllImport(Lib, EntryPoint = "llama_model_free")]
    internal static extern void ModelFree(IntPtr model);

    [DllImport(Lib, EntryPoint = "llama_model_get_vocab")]
    internal static extern IntPtr ModelGetVocab(IntPtr model);

    [DllImport(Lib, EntryPoint = "llama_model_n_ctx_train")]
    internal static extern int ModelNCtxTrain(IntPtr model);

    // ---------- context ----------

    [DllImport(Lib, EntryPoint = "llama_context_default_params")]
    internal static extern ContextParams ContextDefaultParams();

    [DllImport(Lib, EntryPoint = "llama_init_from_model")]
    internal static extern IntPtr InitFromModel(IntPtr model, ContextParams p);

    [DllImport(Lib, EntryPoint = "llama_free")]
    internal static extern void Free(IntPtr ctx);

    [DllImport(Lib, EntryPoint = "llama_n_ctx")]
    internal static extern uint NCtx(IntPtr ctx);

    [DllImport(Lib, EntryPoint = "llama_get_memory")]
    internal static extern IntPtr GetMemory(IntPtr ctx);

    [DllImport(Lib, EntryPoint = "llama_memory_clear")]
    internal static extern void MemoryClear(IntPtr memory, [MarshalAs(UnmanagedType.I1)] bool data);

    /// <summary>Removes tokens of <paramref name="seqId"/> with positions in [p0, p1)
    /// (p1 &lt; 0 → to end). Returns false when a partial removal is unsupported — callers
    /// must fall back to <see cref="MemoryClear"/>. KV prefix reuse (b10189 API).</summary>
    [DllImport(Lib, EntryPoint = "llama_memory_seq_rm")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool MemorySeqRm(IntPtr memory, int seqId, int p0, int p1);

    // ---------- vocab ----------

    [DllImport(Lib, EntryPoint = "llama_vocab_n_tokens")]
    internal static extern int VocabNTokens(IntPtr vocab);

    [DllImport(Lib, EntryPoint = "llama_vocab_is_eog")]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool VocabIsEog(IntPtr vocab, int token);

    [DllImport(Lib, EntryPoint = "llama_tokenize")]
    internal static extern unsafe int Tokenize(IntPtr vocab, byte* text, int textLen, int* tokens, int nTokensMax,
        [MarshalAs(UnmanagedType.I1)] bool addSpecial, [MarshalAs(UnmanagedType.I1)] bool parseSpecial);

    [DllImport(Lib, EntryPoint = "llama_token_to_piece")]
    internal static extern unsafe int TokenToPiece(IntPtr vocab, int token, byte* buf, int length, int lstrip,
        [MarshalAs(UnmanagedType.I1)] bool special);

    // ---------- decode ----------

    [DllImport(Lib, EntryPoint = "llama_batch_get_one")]
    internal static extern unsafe Batch BatchGetOne(int* tokens, int nTokens);

    [DllImport(Lib, EntryPoint = "llama_decode")]
    internal static extern int Decode(IntPtr ctx, Batch batch);

    [DllImport(Lib, EntryPoint = "llama_set_n_threads")]
    internal static extern void SetNThreads(IntPtr ctx, int nThreads, int nThreadsBatch);

    /// <summary>Blocks until all pending backend work completes (needed for honest timing on Metal).</summary>
    [DllImport(Lib, EntryPoint = "llama_synchronize")]
    internal static extern void Synchronize(IntPtr ctx);

    // ---------- sampler chain (b10189 uses llama_sampler_chain_* APIs) ----------

    [DllImport(Lib, EntryPoint = "llama_sampler_chain_default_params")]
    internal static extern SamplerChainParams SamplerChainDefaultParams();

    [DllImport(Lib, EntryPoint = "llama_sampler_chain_init")]
    internal static extern IntPtr SamplerChainInit(SamplerChainParams p);

    [DllImport(Lib, EntryPoint = "llama_sampler_chain_add")]
    internal static extern void SamplerChainAdd(IntPtr chain, IntPtr sampler);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_greedy")]
    internal static extern IntPtr SamplerInitGreedy();

    [DllImport(Lib, EntryPoint = "llama_sampler_init_dist")]
    internal static extern IntPtr SamplerInitDist(uint seed);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_top_k")]
    internal static extern IntPtr SamplerInitTopK(int k);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_top_p")]
    internal static extern IntPtr SamplerInitTopP(float p, nuint minKeep);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_min_p")]
    internal static extern IntPtr SamplerInitMinP(float p, nuint minKeep);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_temp")]
    internal static extern IntPtr SamplerInitTemp(float t);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_penalties")]
    internal static extern IntPtr SamplerInitPenalties(int penaltyLastN, float penaltyRepeat, float penaltyFreq, float penaltyPresent);

    [DllImport(Lib, EntryPoint = "llama_sampler_init_grammar")]
    internal static extern IntPtr SamplerInitGrammar(IntPtr vocab,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string grammarStr,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string grammarRoot);

    /// <summary>Applies the chain to the logits at batch index <paramref name="idx"/> and accepts the token.</summary>
    [DllImport(Lib, EntryPoint = "llama_sampler_sample")]
    internal static extern int SamplerSample(IntPtr sampler, IntPtr ctx, int idx);

    [DllImport(Lib, EntryPoint = "llama_sampler_reset")]
    internal static extern void SamplerReset(IntPtr sampler);

    [DllImport(Lib, EntryPoint = "llama_sampler_free")]
    internal static extern void SamplerFree(IntPtr sampler);
}
