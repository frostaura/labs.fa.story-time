using System.Text.Json;
using SQLite;
using StoryTime.Contracts;
using StoryTime.Core.Abstractions;

namespace StoryTime.Core.Persistence;

/// <summary>IStoryStore over sqlite-net-pcl. One db3 in AppData; audio + poster PNGs live on
/// disk and the DB stores paths. Deleting a story deletes its files too.</summary>
public sealed class SqliteStoryStore : IStoryStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly StoryTimeOptions _options;
    private readonly IStoryCatalog? _catalog;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private SQLiteAsyncConnection? _db;

    /// <summary>The catalog is optional so tests and the harness can new this up bare;
    /// in the app it is always injected, which is what makes "every story mutation
    /// notifies every list" true by construction rather than by discipline.</summary>
    public SqliteStoryStore(StoryTimeOptions options, IStoryCatalog? catalog = null)
    {
        _options = options;
        _catalog = catalog;
    }

    /// <summary>Lazy + thread-safe: every operation self-initializes, so page appearance
    /// racing app startup can never observe an uninitialized store.</summary>
    private async Task<SQLiteAsyncConnection> DbAsync()
    {
        if (_db is not null) return _db;
        await _initLock.WaitAsync();
        try
        {
            if (_db is not null) return _db;
            Directory.CreateDirectory(Path.GetDirectoryName(_options.DatabasePath)!);
            Directory.CreateDirectory(_options.MediaDirectory);
            var db = new SQLiteAsyncConnection(_options.DatabasePath,
                SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.SharedCache);
            await db.CreateTableAsync<ProfileRow>();
            await db.CreateTableAsync<StoryRow>();
            await db.CreateTableAsync<BibleRow>();
            await db.CreateTableAsync<CheckpointRow>();
            _db = db;
            return db;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task InitializeAsync() => _ = await DbAsync();

    // ---------- Profiles ----------

    public async Task<List<ChildProfile>> GetProfilesAsync()
    {
        var db = await DbAsync();
        var rows = await db.Table<ProfileRow>().ToListAsync();
        return rows.Select(ToProfile).ToList();
    }

    public async Task<ChildProfile?> GetActiveProfileAsync()
    {
        var db = await DbAsync();
        var row = await db.Table<ProfileRow>().Where(p => p.IsActive).FirstOrDefaultAsync()
                  ?? await db.Table<ProfileRow>().FirstOrDefaultAsync();
        return row is null ? null : ToProfile(row);
    }

    public async Task SaveProfileAsync(ChildProfile profile)
    {
        var row = new ProfileRow
        {
            Id = profile.Id,
            Name = profile.Name,
            Age = profile.Age,
            Interests = string.Join('\n', profile.Interests),
            Voice = (int)profile.Voice,
            Length = (int)profile.Length,
            Mode = (int)profile.Mode,
            IsActive = true, // single-profile world for now; the saved profile becomes active
        };
        var db = await DbAsync();
        if (row.Id == 0)
        {
            await db.InsertAsync(row);
            profile.Id = row.Id;
        }
        else
        {
            await db.InsertOrReplaceAsync(row);
        }
    }

    // ---------- Stories ----------

    public async Task<List<StoryRecord>> GetStoriesAsync(int profileId)
    {
        var db = await DbAsync();
        var rows = await db.Table<StoryRow>().Where(s => s.ProfileId == profileId)
            .OrderByDescending(s => s.CreatedUtc).ToListAsync();
        return rows.Select(ToStory).ToList();
    }

    public async Task<StoryRecord?> GetStoryAsync(int storyId)
    {
        var db = await DbAsync();
        var row = await db.Table<StoryRow>().Where(s => s.Id == storyId).FirstOrDefaultAsync();
        return row is null ? null : ToStory(row);
    }

    public async Task SaveStoryAsync(StoryRecord story)
    {
        var row = new StoryRow
        {
            Id = story.Id,
            ProfileId = story.ProfileId,
            Title = story.Title,
            Mode = (int)story.Mode,
            EpisodeNumber = story.EpisodeNumber,
            RecapLine = story.RecapLine,
            FullText = story.FullText,
            PosterSpecJson = story.PosterSpecJson,
            ThumbnailPath = story.ThumbnailPath,
            AudioPath = story.AudioPath,
            TeaserPath = story.TeaserPath,
            TimestampsJson = story.TimestampsJson,
            DurationSeconds = story.DurationSeconds,
            Voice = (int)story.Voice,
            State = (int)story.State,
            IsFavorite = story.IsFavorite,
            CreatedUtc = story.CreatedUtc,
        };
        var db = await DbAsync();
        if (row.Id == 0)
        {
            await db.InsertAsync(row);
            story.Id = row.Id;
        }
        else
        {
            await db.InsertOrReplaceAsync(row);
        }
        // Every list in the app hangs off this — see IStoryCatalog.
        _catalog?.NotifyChanged(StoryChangeKind.Saved, story.Id, story.ProfileId);
    }

    public async Task DeleteStoryAsync(int storyId)
    {
        var db = await DbAsync();
        var row = await db.Table<StoryRow>().Where(s => s.Id == storyId).FirstOrDefaultAsync();
        if (row is null) return;
        var ambientPath = Audio.AmbientTrackProvider.PathFor(_options, row.Id);
        foreach (var path in new[] { row.AudioPath, row.TeaserPath, row.ThumbnailPath, ambientPath })
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { /* best-effort file cleanup; the row still goes */ }
            catch (UnauthorizedAccessException) { }
        }
        await db.DeleteAsync<StoryRow>(storyId);
        _catalog?.NotifyChanged(StoryChangeKind.Removed, storyId, row.ProfileId);
    }

    // ---------- Bible ----------

    public async Task<StoryBible?> GetBibleAsync(int profileId)
    {
        var db = await DbAsync();
        var row = await db.Table<BibleRow>().Where(b => b.ProfileId == profileId).FirstOrDefaultAsync();
        return row is null ? null : JsonSerializer.Deserialize<StoryBible>(row.Json, JsonOpts);
    }

    public async Task SaveBibleAsync(int profileId, StoryBible bible)
    {
        var db = await DbAsync();
        await db.InsertOrReplaceAsync(new BibleRow { ProfileId = profileId, Json = JsonSerializer.Serialize(bible, JsonOpts) });
    }

    // ---------- Checkpoint ----------

    public async Task<WeaveCheckpoint?> GetCheckpointAsync()
    {
        var db = await DbAsync();
        var row = await db.Table<CheckpointRow>().FirstOrDefaultAsync();
        return row is null || string.IsNullOrEmpty(row.Json)
            ? null
            : JsonSerializer.Deserialize<WeaveCheckpoint>(row.Json, JsonOpts);
    }

    public async Task SaveCheckpointAsync(WeaveCheckpoint? checkpoint)
    {
        var db = await DbAsync();
        if (checkpoint is null)
            await db.DeleteAsync<CheckpointRow>(1);
        else
            await db.InsertOrReplaceAsync(new CheckpointRow { Id = 1, Json = JsonSerializer.Serialize(checkpoint, JsonOpts) });
    }

    // ---------- Storage ----------

    public async Task<long> GetStoredBytesAsync()
    {
        var db = await DbAsync();
        long total = 0;
        if (File.Exists(_options.DatabasePath)) total += new FileInfo(_options.DatabasePath).Length;
        var rows = await db.Table<StoryRow>().ToListAsync();
        foreach (var row in rows)
        {
            foreach (var path in new[] { row.AudioPath, row.TeaserPath, row.ThumbnailPath })
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    total += new FileInfo(path).Length;
            }
        }
        return total;
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.CloseAsync();
            _db = null;
        }
    }

    private static ChildProfile ToProfile(ProfileRow row) => new()
    {
        Id = row.Id,
        Name = row.Name,
        Age = row.Age,
        Interests = row.Interests.Length == 0 ? [] : [.. row.Interests.Split('\n')],
        Voice = (NarratorVoice)row.Voice,
        Length = (StoryLength)row.Length,
        Mode = (StoryMode)row.Mode,
    };

    private static StoryRecord ToStory(StoryRow row) => new()
    {
        Id = row.Id,
        ProfileId = row.ProfileId,
        Title = row.Title,
        Mode = (StoryMode)row.Mode,
        EpisodeNumber = row.EpisodeNumber,
        RecapLine = row.RecapLine,
        FullText = row.FullText,
        PosterSpecJson = row.PosterSpecJson,
        ThumbnailPath = row.ThumbnailPath,
        AudioPath = row.AudioPath,
        TeaserPath = row.TeaserPath,
        TimestampsJson = row.TimestampsJson,
        DurationSeconds = row.DurationSeconds,
        Voice = (NarratorVoice)row.Voice,
        State = (StoryState)row.State,
        IsFavorite = row.IsFavorite,
        CreatedUtc = row.CreatedUtc,
    };
}
