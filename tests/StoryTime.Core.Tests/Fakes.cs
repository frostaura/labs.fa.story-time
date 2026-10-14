using StoryTime.Contracts;
using StoryTime.Core;
using StoryTime.Core.Abstractions;
using StoryTime.Core.Persistence;
using StoryTime.Core.Services;

namespace StoryTime.Core.Tests;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 7, 30, 19, 0, 0, DateTimeKind.Utc);
    public long MonotonicMs { get; set; } = 1_000_000;

    public void Advance(TimeSpan span)
    {
        UtcNow += span;
        MonotonicMs += (long)span.TotalMilliseconds;
    }
}

public sealed class FakeTtsEngine : ITtsEngine
{
    public List<int> SynthesizeCallSentenceCounts { get; } = [];
    public List<string> OutputPaths { get; } = [];

    public string Name => "FakeTts";
    public bool HasPersonaVoices => false;

    /// <summary>Optional hook fired at the START of every synthesis call (overlap tests
    /// coordinate producer/consumer ordering through it).</summary>
    public Action<string>? OnSynthesizeStart { get; set; }

    /// <summary>Sample rate of the tiny-but-real WAVs this fake writes (the overlapped
    /// pipeline concatenates part files, so the bytes must be genuine PCM).</summary>
    public const int SampleRate = 100;

    public Task<NarrationManifest> SynthesizeAsync(IReadOnlyList<string> sentences, NarratorVoice voice,
        string outputPath, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        OnSynthesizeStart?.Invoke(outputPath);
        SynthesizeCallSentenceCounts.Add(sentences.Count);
        OutputPaths.Add(outputPath);

        var timestamps = new List<SentenceTimestamp>();
        double t = 0;
        for (var i = 0; i < sentences.Count; i++)
        {
            var dur = 2.0;
            timestamps.Add(new SentenceTimestamp { Index = i, StartSeconds = t, EndSeconds = t + dur });
            t += dur;
        }
        // Real (silent) PCM so WavAudio.Concat/ReadDurationSeconds work on the output.
        StoryTime.Core.Engines.Narration.WavAudio.Write(outputPath,
            [new short[(int)(t * SampleRate)]], SampleRate);
        progress?.Report(0.5);
        progress?.Report(1.0);
        return Task.FromResult(new NarrationManifest
        {
            AudioPath = outputPath,
            TeaserPath = string.Empty,
            DurationSeconds = t,
            Timestamps = timestamps,
        });
    }

    public Task<string> RenderVoiceSampleAsync(NarratorVoice voice, CancellationToken ct) =>
        Task.FromResult(string.Empty);
}

public sealed class FakeNotificationService : INotificationService
{
    public int ShownCount { get; private set; }
    public string? LastTitle { get; private set; }

    public Task<bool> RequestPermissionAsync() => Task.FromResult(true);

    public Task ShowStoryReadyAsync(string childName, string storyTitle)
    {
        ShownCount++;
        LastTitle = storyTitle;
        return Task.CompletedTask;
    }
}

public sealed class FakeNavigator : INavigator
{
    public List<string> Routes { get; } = [];
    public int BackCount { get; private set; }

    public Task GoToAsync(string route)
    {
        Routes.Add(route);
        return Task.CompletedTask;
    }

    public Task GoBackAsync()
    {
        BackCount++;
        return Task.CompletedTask;
    }
}

/// <summary>Recording haptics fake — every call appends its semantic name, so tests can
/// assert both counts and ordering of the full seven-word vocabulary.</summary>
public sealed class FakeHaptics : IHaptics
{
    public List<string> Calls { get; } = [];

    public int Count(string name) => Calls.Count(c => c == name);

    public void Light() => Calls.Add(nameof(Light));
    public void Selection() => Calls.Add(nameof(Selection));
    public void Medium() => Calls.Add(nameof(Medium));
    public void Success() => Calls.Add(nameof(Success));
    public void Warning() => Calls.Add(nameof(Warning));
    public void Tick() => Calls.Add(nameof(Tick));
    public void Celebrate() => Calls.Add(nameof(Celebrate));
}

public sealed class FakeBiometricGate : IBiometricGate
{
    public bool Result { get; set; } = true;
    public string MechanismName { get; set; } = "Face ID";

    public Task<bool> AuthenticateAsync(string reason, CancellationToken ct) => Task.FromResult(Result);
}

public sealed class FakeWeaveHost : IWeaveHost
{
    public bool IsWeaving { get; set; }
    public WeaveProgress? CurrentProgress { get; set; }
    public StoryRecord? LastCompleted { get; set; }
    public string? LastFailureMessage { get; set; }
    public ChildProfile? StartedWith { get; private set; }

    public event EventHandler<WeaveProgress>? ProgressChanged;
    public event EventHandler<StoryRecord>? WeaveCompleted;
    public event EventHandler<string>? WeaveFailed;

    public Task StartAsync(ChildProfile profile)
    {
        StartedWith = profile;
        IsWeaving = true;
        return Task.CompletedTask;
    }

    public Task TryResumeAsync() => Task.CompletedTask;

    public Task CancelAsync()
    {
        IsWeaving = false;
        return Task.CompletedTask;
    }

    public event EventHandler? WeaveCancelled;
    public bool DiscardCalled { get; private set; }

    public Task CancelAndDiscardAsync()
    {
        DiscardCalled = true;
        IsWeaving = false;
        CurrentProgress = null;
        WeaveCancelled?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void RaiseProgress(WeaveProgress p)
    {
        CurrentProgress = p;
        ProgressChanged?.Invoke(this, p);
    }

    public void RaiseCompleted(StoryRecord story)
    {
        IsWeaving = false;
        LastCompleted = story;
        WeaveCompleted?.Invoke(this, story);
    }

    public void RaiseFailed(string message)
    {
        IsWeaving = false;
        LastFailureMessage = message;
        WeaveFailed?.Invoke(this, message);
    }
}

public sealed class FakeNarrationPlayer : INarrationPlayer
{
    public StoryRecord? Loaded { get; private set; }
    public bool LoadedTeaserOnly { get; private set; }
    public bool IsPlaying { get; private set; }
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; set; } = 120;
    public double Volume { get; set; } = 1.0;
    public bool SupportsVolume { get; set; } = true;

    public event EventHandler? PositionChanged;
    public event EventHandler? Completed;

    public bool CanPlay(StoryRecord story) => true;

    public Task LoadAsync(StoryRecord story, bool teaserOnly, CancellationToken ct)
    {
        Loaded = story;
        LoadedTeaserOnly = teaserOnly;
        return Task.CompletedTask;
    }

    public Task PlayAsync() { IsPlaying = true; return Task.CompletedTask; }
    public Task PauseAsync() { IsPlaying = false; return Task.CompletedTask; }
    public Task StopAsync() { IsPlaying = false; PositionSeconds = 0; return Task.CompletedTask; }

    public Task SeekAsync(double seconds)
    {
        PositionSeconds = seconds;
        PositionChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public void RaiseCompleted() => Completed?.Invoke(this, EventArgs.Empty);
}

public sealed class FakePlayerProvider : INarrationPlayerProvider
{
    public FakeNarrationPlayer Player { get; } = new();
    public INarrationPlayer GetFor(StoryRecord story) => Player;
}

public sealed class FakeAmbientPlayer : IAmbientPlayer
{
    public bool IsPlaying { get; private set; }
    public string? StartedPath { get; private set; }
    public double LastVolume { get; private set; } = -1;
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public Task StartAsync(string wavPath, double volume, CancellationToken ct)
    {
        IsPlaying = true;
        StartedPath = wavPath;
        LastVolume = volume;
        StartCount++;
        return Task.CompletedTask;
    }

    public void SetVolume(double volume) => LastVolume = volume;

    public Task StopAsync()
    {
        IsPlaying = false;
        StopCount++;
        return Task.CompletedTask;
    }
}

public sealed class FakeEntitlements : IEntitlements
{
    public bool IsPremium { get; set; }
    public int TrialNightsLeft { get; set; } = 7;
    public int CooldownMinutes => IsPremium ? 15 : 30;
    public bool CanUseLength(StoryLength length) => IsPremium || length != StoryLength.Fifteen;
}

/// <summary>Temp-directory environment with a real sqlite store.</summary>
public sealed class TestEnv : IAsyncDisposable
{
    public StoryTimeOptions Options { get; }
    public SqliteStoryStore Store { get; }

    /// <summary>The live story-change seam (IStoryCatalog). Raises inline on the calling
    /// thread here — no dispatcher — so a test can await the reload it triggered.</summary>
    public StoryCatalog Catalog { get; } = new();

    private readonly string _root;

    private TestEnv()
    {
        _root = Path.Combine(Path.GetTempPath(), "tw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        Options = new StoryTimeOptions
        {
            DatabasePath = Path.Combine(_root, "storytime.db3"),
            MediaDirectory = Path.Combine(_root, "media"),
        };
        Store = new SqliteStoryStore(Options, Catalog);
    }

    public static async Task<TestEnv> CreateAsync()
    {
        var env = new TestEnv();
        await env.Store.InitializeAsync();
        return env;
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    public static ChildProfile Profile(StoryMode mode = StoryMode.Series, StoryLength length = StoryLength.Five, int age = 5) => new()
    {
        Name = "Aria",
        Age = age,
        Interests = ["Foxes", "Ocean"],
        Voice = NarratorVoice.Ember,
        Length = length,
        Mode = mode,
    };
}

/// <summary>Scriptable IPremiumStore. Purchase/restore outcomes run through a real
/// PremiumLedger, exactly as the StoreKit implementation does.</summary>
public sealed class FakePremiumStore : IPremiumStore
{
    private readonly PremiumLedger _ledger;

    public FakePremiumStore(PremiumLedger ledger) => _ledger = ledger;

    public PremiumProductInfo Product { get; set; } = new(PremiumProductState.Available, "R 49,99");
    public PremiumPurchaseResult NextPurchase { get; set; } = PremiumPurchaseResult.Purchased;
    public PremiumPurchaseResult NextRestore { get; set; } = PremiumPurchaseResult.NothingToRestore;
    public int LoadCount { get; private set; }
    public int PurchaseCount { get; private set; }
    public int RestoreCount { get; private set; }
    public int SubscriberCount => EntitlementChanged?.GetInvocationList().Length ?? 0;

    public Task<PremiumProductInfo> LoadProductAsync(CancellationToken ct)
    {
        LoadCount++;
        return Task.FromResult(Product);
    }

    public Task<PremiumPurchaseResult> PurchaseAsync(CancellationToken ct)
    {
        PurchaseCount++;
        if (NextPurchase == PremiumPurchaseResult.Purchased) _ledger.RecordGranted(PremiumProduct.Id);
        return Task.FromResult(NextPurchase);
    }

    public Task<PremiumPurchaseResult> RestoreAsync(CancellationToken ct)
    {
        RestoreCount++;
        if (NextRestore == PremiumPurchaseResult.Restored) _ledger.RecordRestoreFinished(sawPremium: true);
        else if (NextRestore == PremiumPurchaseResult.NothingToRestore) _ledger.RecordRestoreFinished(sawPremium: false);
        return Task.FromResult(NextRestore);
    }

    /// <summary>Simulates an out-of-band transaction (Ask to Buy approval, refund).</summary>
    public void RaiseOutOfBand(bool granted)
    {
        if (granted) _ledger.RecordGranted(PremiumProduct.Id);
        else _ledger.RecordRevoked([PremiumProduct.Id]);
        EntitlementChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? EntitlementChanged;

    /// <summary>A PremiumViewModel over a fake store sharing <paramref name="settings"/>.</summary>
    public static ViewModels.PremiumViewModel CreateViewModel(ISettingsStore settings, INavigator navigator,
        IHaptics haptics, FakePremiumStore? store = null)
    {
        var ledger = new PremiumLedger(settings);
        return new ViewModels.PremiumViewModel(settings, navigator, haptics, store ?? new FakePremiumStore(ledger), ledger);
    }
}
