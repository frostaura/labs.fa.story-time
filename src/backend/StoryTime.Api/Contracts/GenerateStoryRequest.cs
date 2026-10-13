using StoryTime.Api.Domain;

namespace StoryTime.Api.Contracts;

public sealed record GenerateStoryRequest(
    string SoftUserId,
    string? ChildName,
    string Mode,
    int DurationMinutes,
    string? SeriesId,
    bool? ApprovalRequired,
    bool Favorite,
    bool ReducedMotion = false,
    OneShotCustomizationRequest? Customization = null,
    StoryBibleSnapshot? StoryBible = null,
    OnDeviceDraftRequest? OnDeviceDraft = null);

public sealed record OneShotCustomizationRequest(
    string? ArcName,
    string? CompanionName,
    string? Setting,
    string? Mood,
    string? ThemeTrackId,
    string? NarrationStyle);

/// <summary>
/// Story text drafted on-device by Apple Intelligence (Foundation Models) in the iOS host app.
/// When present and enabled, it replaces server-side text composition; the API still owns
/// policy, media, approval, continuity, and library state.
/// </summary>
public sealed record OnDeviceDraftRequest(
    string? Title,
    IReadOnlyList<string>? Scenes,
    string? Provider);
