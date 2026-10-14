using StoryTime.Contracts;

namespace StoryTime.Core.Abstractions;

// ============ Engine seams (binding — see docs/architecture/overview.md §2) ============

/// <summary>Text generation: outline, scene prose, polish, poster spec. Implementations:
/// TemplateStoryEngine (deterministic, always available — the current default) and, from the
/// Apple-models lane, an on-device Apple Foundation Models engine (iOS 26+). Swap point:
/// MauiProgram.RegisterStoryEngine.</summary>
public interface IStoryEngine
{
    string Name { get; }
    Task<StoryOutline> GenerateOutlineAsync(ChildProfile profile, StoryBible? bible, int sceneCount, CancellationToken ct);
    Task<string> GenerateSceneAsync(ChildProfile profile, StoryOutline outline, SceneOutline scene, string previousTail, CancellationToken ct);
    Task<string> PolishAsync(ChildProfile profile, string stitchedText, CancellationToken ct);
    /// <summary>Window-scoped polish for the overlapped pipeline (additive 2026-07-31):
    /// polish ~2 scenes as one passage. Only the FINAL window may close the story with the
    /// goodnight paragraph — middle windows must stay open-ended (settled, never a
    /// goodnight). Default falls back to whole-text PolishAsync so existing engines and
    /// fakes keep working; real engines override with window-aware prompting.</summary>
    Task<string> PolishWindowAsync(ChildProfile profile, string windowText, string previousTail, bool isFinalWindow, CancellationToken ct)
        => PolishAsync(profile, windowText, ct);
    Task<PosterSpec> GeneratePosterSpecAsync(StoryOutline outline, CancellationToken ct);
    /// <summary>Rewrite a flagged passage gently, preserving plot. Used by the safety filter.</summary>
    Task<string> RepairPassageAsync(ChildProfile profile, string passage, string reason, CancellationToken ct);
}

/// <summary>Sentence-batch TTS. Implementations: PlatformTtsEngine (MAUI TextToSpeech — the
/// current default) and, from the narration lane, an AVSpeechSynthesizer engine that renders
/// to PCM and runs it through Engines/Narration (ProsodyDirector + AudioShaper cozy
/// mastering). Swap point: MauiProgram.RegisterTtsEngine.</summary>
public interface ITtsEngine
{
    string Name { get; }
    /// <summary>True when this engine can render the three consistent personas.</summary>
    bool HasPersonaVoices { get; }
    /// <summary>Render sentences to a single audio file; returns manifest with per-sentence timestamps.</summary>
    Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct);
    /// <summary>Paragraph-aware synthesis: engines that shape prosody (ProsodyDirector) honor
    /// paragraph boundaries as longer scene-beat pauses. Default falls back to the flat call,
    /// so existing engines (PlatformTtsEngine) need no change.</summary>
    Task<NarrationManifest> SynthesizeAsync(NarrationScript script, NarratorVoice voice, string outputPath,
        IProgress<double>? progress, CancellationToken ct)
        => SynthesizeAsync(script.SentenceList, voice, outputPath, progress, ct);
    /// <summary>Short spoken sample for the narrator picker.</summary>
    Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct);
}

/// <summary>Narration input with structure preserved: ordered sentences plus the indices
/// that open a new paragraph (index 0 excluded). Produced by SentenceSplitter.SplitScript.</summary>
public sealed class NarrationScript
{
    public required List<string> SentenceList { get; init; }
    public required HashSet<int> ParagraphStartSet { get; init; }
    public IReadOnlyList<string> Sentences => SentenceList;

    /// <summary>Silence prepended before the first sentence (seconds). The overlapped
    /// pipeline uses it to carry the inter-window paragraph beat at the head of each
    /// window ≥ 1, so concatenated part WAVs keep a scene-beat pause across the seam.
    /// Engines that ignore it (default interface overload, platform TTS) lose only the
    /// beat, never a sentence. Additive 2026-07-31.</summary>
    public double LeadingSilenceSeconds { get; init; }

    /// <summary>Whether to append the end-of-story tail silence. False for every
    /// overlapped window except the last, so the tail exists exactly once in the
    /// concatenated story. Additive 2026-07-31.</summary>
    public bool AppendFinalTail { get; init; } = true;

    /// <summary>Script for the first <paramref name="count"/> sentences (teaser).</summary>
    public NarrationScript Take(int count) => new()
    {
        SentenceList = SentenceList.Take(count).ToList(),
        ParagraphStartSet = ParagraphStartSet.Where(i => i < count).ToHashSet(),
    };
}

/// <summary>Validates a PosterSpec and resolves it into deterministic draw geometry.
/// Rasterization happens in the App layer (SkiaPosterRenderer) from this resolved layout.</summary>
public interface IPosterComposer
{
    PosterSpec Validate(PosterSpec spec);            // clamps ranges, enforces exactly 4 layers, falls back safely
    ResolvedPoster Resolve(PosterSpec spec);         // palette lookup + seeded scatter → concrete elements
}

public sealed class ResolvedPosterElement
{
    public PosterShape Shape { get; init; }
    public double X { get; init; }                   // 0..1 within layer canvas
    public double Y { get; init; }                   // anchor: grounded shapes anchor at their base
    public double Scale { get; init; }
    /// <summary>Scatter/cluster density carried through for multi-part painters (pine clusters).</summary>
    public double Density { get; init; }
    public string ColorHex { get; init; } = "#F3E9DC";
    /// <summary>Second color for two-tone painters: water waves, lighthouse beam,
    /// window-glow cottage silhouette. Null → painter's single-color path.</summary>
    public string? SecondaryColorHex { get; init; }
    /// <summary>Soft radial glow strength 0..1 rendered behind/around the shape.
    /// The glow color is the element color unless GlowColorHex overrides it.</summary>
    public double Glow { get; init; }
    public string? GlowColorHex { get; init; }
    /// <summary>Element opacity 0..1 (water reflections, mist). 0 defaults to opaque.</summary>
    public double Alpha { get; init; } = 1.0;
    /// <summary>Mirror the painter horizontally around its anchor.</summary>
    public bool FlipX { get; init; }
    /// <summary>Mirror vertically around the anchor (moonglade / reflections).</summary>
    public bool FlipY { get; init; }
}

public sealed class ResolvedPosterLayer
{
    public int Depth { get; init; }
    public string BackgroundHex { get; init; } = "transparent"; // depth 0 carries the sky fill
    /// <summary>When both are set (depth 0), the renderer paints a vertical gradient
    /// top→bottom instead of the flat BackgroundHex. BackgroundHex stays as the mid tone.</summary>
    public string? BackgroundTopHex { get; init; }
    public string? BackgroundBottomHex { get; init; }
    public double ParallaxTravel { get; init; }                 // max px offset at this depth
    public List<ResolvedPosterElement> Elements { get; init; } = [];
}

public sealed class ResolvedPoster
{
    public List<ResolvedPosterLayer> Layers { get; init; } = [];
    public string BaseHex { get; init; } = "#3A2E52";
}

// ============ Platform seams (implemented in StoryTime.App/Platforms) ============

public interface IBiometricGate
{
    Task<bool> AuthenticateAsync(string reason, CancellationToken ct);
    /// <summary>Face ID / Fingerprint / None — for the settings badge.</summary>
    string MechanismName { get; }
}

public interface INotificationService
{
    Task<bool> RequestPermissionAsync();
    Task ShowStoryReadyAsync(string childName, string storyTitle);
}

/// <summary>Semantic haptic feedback. The platform impl maps to UIFeedbackGenerator, honours
/// the OS haptics settings and no-ops gracefully when hardware is absent. NullHaptics serves
/// tests.
/// Wire semantically, never on scroll or passive events.</summary>
public interface IHaptics
{
    /// <summary>Small tap acknowledgement — CTAs, outlined buttons, gate pass.</summary>
    void Light();
    /// <summary>Picker/selection tick — ages, interests, voices, toggles, slider detents.</summary>
    void Selection();
    /// <summary>A committed action of weight — weave start.</summary>
    void Medium();
    /// <summary>Something finished well — story ready, approval, shelf unlock.</summary>
    void Success();
    /// <summary>Gentle bump — gate fail, premium-gated attempt.</summary>
    void Warning();
    /// <summary>The lightest possible tap, for continuous interactions: carousel snap
    /// changes, scrub steps, mix-slider quarter steps, weave stage transitions, hold
    /// progress. Platform impls coalesce calls to ≥60 ms apart so scrubbing can't buzz;
    /// only ever fire it from an actual user gesture or a foreground-gated event.</summary>
    void Tick();
    /// <summary>Gentle two-beat completion pattern — story ready, approval unlock,
    /// premium unlock. Softer than a notification-success thud; never a harsh
    /// double-click.</summary>
    void Celebrate();
}

/// <summary>How the pipeline schedules its stages (additive 2026-07-31).</summary>
public enum WeaveStrategy
{
    /// <summary>Classic order: all scenes → whole-story polish → poster → narration.
    /// Text and speech engines are never co-resident — the memory-safe path for low-RAM devices.</summary>
    Sequential,
    /// <summary>Producer–consumer: polish per ~2-scene window as scenes complete; narration
    /// consumes polished windows while later scenes still generate; poster runs after the
    /// final scene's text work, in parallel with the remaining narration. Text + speech
    /// engines are co-resident BY DESIGN on capable devices.</summary>
    Overlapped,
}

/// <summary>Device memory signal for the strategy pick. The Core default reads the GC's
/// view of total addressable memory; the App head may pre-register a richer
/// implementation (NSProcessInfo). Additive 2026-07-31.</summary>
public interface IDeviceCapabilities
{
    long PhysicalMemoryBytes { get; }
    /// <summary>Platform low-RAM flag; Core default is false.</summary>
    bool IsLowMemoryDevice { get; }
    /// <summary>Logical processor count. Overlap needs real CPU headroom: on ≤ 4-core
    /// devices co-resident text + speech work thrashes and sequential is FASTER
    /// (measured 2026-07-31; re-measure once the Apple engines land).</summary>
    int ProcessorCount => Environment.ProcessorCount;
}

public sealed class DefaultDeviceCapabilities : IDeviceCapabilities
{
    /// <summary>Device memory as the GC sees it — on iOS this reports physical memory.</summary>
    public long PhysicalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    public bool IsLowMemoryDevice => false;
}

/// <summary>WAV → m4a/AAC via the platform encoder; identity fallback returns the wav.</summary>
public interface IAudioEncoder
{
    Task<string> EncodeToCompressedAsync(string wavPath, CancellationToken ct);
}

// ============ Core services ============

public interface IClock
{
    DateTime UtcNow { get; }
    /// <summary>Monotonic milliseconds — cooldown anchor resistant to wall-clock fiddling.</summary>
    long MonotonicMs { get; }
}

public interface IStoryStore
{
    Task InitializeAsync();
    Task<List<ChildProfile>> GetProfilesAsync();
    Task<ChildProfile?> GetActiveProfileAsync();
    Task SaveProfileAsync(ChildProfile profile);
    Task<List<StoryRecord>> GetStoriesAsync(int profileId);
    Task<StoryRecord?> GetStoryAsync(int storyId);
    Task SaveStoryAsync(StoryRecord story);
    Task DeleteStoryAsync(int storyId);                 // also removes audio + art files
    Task<StoryBible?> GetBibleAsync(int profileId);
    Task SaveBibleAsync(int profileId, StoryBible bible);
    Task<WeaveCheckpoint?> GetCheckpointAsync();
    Task SaveCheckpointAsync(WeaveCheckpoint? checkpoint); // null clears
    Task<long> GetStoredBytesAsync();
}

public interface ISettingsStore
{
    bool OnboardingComplete { get; set; }
    bool ApprovalStep { get; set; }                     // default true
    bool StoryReadyNotifications { get; set; }          // default true
    // NOTE: QualityAnalytics was removed entirely by founder decision 2026-07-31.
    // StoryTime collects nothing, not even locally, and has no analytics seam to re-add.
    DateTime? FirstRunUtc { get; set; }
    long? CooldownAnchorMonotonicMs { get; set; }
    DateTime? CooldownEndsUtc { get; set; }
    /// <summary>Debug-only local premium unlock (the PremiumPage testing row). Read by
    /// StoreEntitlements. Harmless if set in Release — the UI that flips it is compiled
    /// out of Release builds.</summary>
    bool DebugPremiumOverride { get; set; }
    /// <summary>True once the one-off Premium purchase was bought or restored on this device.
    /// Written only by PremiumLedger from App Store transactions; re-validated by Restore.</summary>
    bool PremiumPurchased { get; set; }
    /// <summary>Premium ambient-bed mix level 0..1, persisted per app (not per story).
    /// Default 0.3 — the bed is meant to sit far under the voice. (Additive, 2026-07-30.)</summary>
    double MusicVolume { get; set; }
    /// <summary>Content filter level (Settings → Content filters). Default Gentle. Only
    /// widens the allowed-theme palette for NEW weaves; every hard safety layer is
    /// level-independent by construction. (Additive, 2026-07-30.)</summary>
    ContentFilterLevel ContentFilter { get; set; }
}

public interface IEntitlements
{
    bool IsPremium { get; }                             // bought/restored Premium (or the Debug testing unlock)
    int TrialNightsLeft { get; }
    int CooldownMinutes { get; }                        // 30, or 15 when premium
    bool CanUseLength(StoryLength length);
}
