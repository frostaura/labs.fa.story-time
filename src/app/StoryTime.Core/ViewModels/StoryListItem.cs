using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StoryTime.Contracts;

namespace StoryTime.Core.ViewModels;

/// <summary>Row/tile presentation of a stored story for Home, Shelf and Library.</summary>
public sealed partial class StoryListItem : ObservableObject
{
    public StoryListItem(StoryRecord record)
    {
        Record = record;
        IsFavorite = record.IsFavorite;
    }

    public StoryRecord Record { get; private set; }

    public int Id => Record.Id;
    public string Title => Record.Title;
    public string ThumbnailPath => Record.ThumbnailPath;
    public bool HasThumbnail => !string.IsNullOrEmpty(Record.ThumbnailPath);

    private string? _parsedSpecJson;
    private PosterSpec? _poster;

    /// <summary>The story's poster spec (parsed once per stored JSON) — cards and shelf
    /// tiles render its parallax layers; the flattened thumbnail shows while they render.</summary>
    public PosterSpec? Poster
    {
        get
        {
            if (!ReferenceEquals(_parsedSpecJson, Record.PosterSpecJson))
            {
                _parsedSpecJson = Record.PosterSpecJson;
                _poster = StoryTime.Poster.Spec.PosterSpecJson.Parse(Record.PosterSpecJson);
            }
            return _poster;
        }
    }

    [ObservableProperty]
    private bool _isFavorite;

    public string MetaLine
    {
        get
        {
            var minutes = Record.DurationSeconds > 0 ? Math.Max(1, (int)Math.Round(Record.DurationSeconds / 60)) : 0;
            var length = minutes > 0 ? $"{minutes} min" : "story";
            var prefix = Record.State switch
            {
                StoryState.Failed => "didn't finish",
                StoryState.AwaitingApproval => "waiting for you",
                StoryState.Weaving => "weaving…",
                _ => Record.Mode == StoryMode.Series && Record.EpisodeNumber > 0
                    ? $"Series · Episode {Record.EpisodeNumber}"
                    : "One-shot",
            };
            // Favorites carry an amber ♥ beside the title (mockup style), not in the meta.
            return $"{prefix} · {length}";
        }
    }

    partial void OnIsFavoriteChanged(bool value) => OnPropertyChanged(nameof(MetaLine));

    /// <summary>Re-point this row at a freshly-read record (same story id) and re-render.
    /// Used by <see cref="SyncInto"/> so a live list can absorb a state change — weaving →
    /// awaiting approval → ready, a new title, a poster that has just been rasterized —
    /// without discarding and rebuilding the row.</summary>
    public void Refresh(StoryRecord record)
    {
        var posterChanged = record.PosterSpecJson != Record.PosterSpecJson;
        Record = record;
        IsFavorite = record.IsFavorite;
        if (posterChanged) OnPropertyChanged(nameof(Poster));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ThumbnailPath));
        OnPropertyChanged(nameof(HasThumbnail));
        OnPropertyChanged(nameof(MetaLine));
    }

    /// <summary>Reconcile a bound collection against a freshly-read, already-ordered list,
    /// keyed on story id: existing rows are refreshed and moved, new ones inserted, gone
    /// ones trimmed.
    ///
    /// Why not Clear() + re-add: every story write during a weave notifies every list
    /// (IStoryCatalog), and a full rebuild would reset the Home carousel's scroll offset
    /// six times per weave — and de-prime its snap-tick heuristic each time. In-place
    /// reconciliation keeps the finger's position and only animates real differences.</summary>
    public static void SyncInto(ObservableCollection<StoryListItem> target, IReadOnlyList<StoryRecord> source)
    {
        for (var i = 0; i < source.Count; i++)
        {
            if (i < target.Count && target[i].Id == source[i].Id)
            {
                target[i].Refresh(source[i]);
                continue;
            }

            var existing = -1;
            for (var j = i; j < target.Count; j++)
            {
                if (target[j].Id == source[i].Id) { existing = j; break; }
            }

            if (existing >= 0)
            {
                target[existing].Refresh(source[i]);
                target.Move(existing, i);
            }
            else
            {
                target.Insert(i, new StoryListItem(source[i]));
            }
        }

        while (target.Count > source.Count) target.RemoveAt(target.Count - 1);
    }
}
