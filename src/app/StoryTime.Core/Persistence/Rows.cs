using SQLite;

namespace StoryTime.Core.Persistence;

// sqlite-net row mirrors — Contracts DTOs stay attribute-free and dependency-free.

[Table("profiles")]
public sealed class ProfileRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    /// <summary>Interests joined with '\n'.</summary>
    public string Interests { get; set; } = string.Empty;
    public int Voice { get; set; }
    public int Length { get; set; }
    public int Mode { get; set; }
    public bool IsActive { get; set; }
}

[Table("stories")]
public sealed class StoryRow
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    [Indexed] public int ProfileId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Mode { get; set; }
    public int EpisodeNumber { get; set; }
    public string RecapLine { get; set; } = string.Empty;
    public string FullText { get; set; } = string.Empty;
    public string PosterSpecJson { get; set; } = string.Empty;
    public string ThumbnailPath { get; set; } = string.Empty;
    public string AudioPath { get; set; } = string.Empty;
    public string TeaserPath { get; set; } = string.Empty;
    public string TimestampsJson { get; set; } = string.Empty;
    public double DurationSeconds { get; set; }
    public int Voice { get; set; }
    public int State { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime CreatedUtc { get; set; }
}

[Table("bibles")]
public sealed class BibleRow
{
    [PrimaryKey] public int ProfileId { get; set; }
    public string Json { get; set; } = string.Empty;
}

[Table("checkpoints")]
public sealed class CheckpointRow
{
    /// <summary>Single-row table (Id = 1) — at most one story run is in flight.</summary>
    [PrimaryKey] public int Id { get; set; } = 1;
    public string Json { get; set; } = string.Empty;
}
