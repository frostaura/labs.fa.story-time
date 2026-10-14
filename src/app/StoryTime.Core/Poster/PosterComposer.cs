using StoryTime.Contracts;
using StoryTime.Core.Abstractions;
using StoryTime.Poster;
using StoryTime.Poster.Spec;

namespace StoryTime.Core.Poster;

/// <summary>Core's poster seam over the procedural engine (src/app/StoryTime.Poster):
/// validation is the spec contract's repair rules; rendering lives in the engine and is
/// reached through <see cref="IPosterRasterizer"/> (thumbnails) and the app's parallax view.
/// See docs/design/poster-engine.md.</summary>
public sealed class PosterComposer : IPosterComposer
{
    /// <summary>Stamped into thumbnail filenames (…-thumb-v{N}.png) so cached art
    /// re-renders whenever the engine's output changes.</summary>
    public const int ArtVersion = PosterEngine.ArtVersion;

    public PosterSpec Validate(PosterSpec spec) => PosterSpecRules.Normalize(spec);
}
