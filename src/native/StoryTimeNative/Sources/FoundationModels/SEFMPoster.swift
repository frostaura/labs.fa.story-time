import Foundation
import FoundationModels

/// Poster art-direction choice. Deliberately tiny and enum-only: the C# side
/// (StoryTime.Core Engines/AppleFoundation/PosterSpecMapper.cs) maps it onto the poster
/// contract, so a poster-contract change only touches this type and that one mapper.
@Generable
struct SEFMPosterChoice {
    @Guide(description: "The colour palette that best matches the story's place and mood",
           .anyOf(["NightHarbor", "MoonMeadow", "OceanDeep", "PlumDusk", "PineForest", "CloudDrift",
                   "StarfieldViolet", "LanternGlow", "TidePool", "SnowHush", "EmberSky", "MistValley"]))
    var palette: String

    @Guide(description: "The light in the sky", .anyOf(["Moon", "Crescent"]))
    var skyLight: String

    @Guide(description: "The far landscape", .anyOf(["Hills", "Mountain", "ForestRow", "Water"]))
    var landscape: String

    @Guide(description: "The one main thing in front: a creature or a lit place from this story",
           .anyOf(["FoxSilhouette", "Rabbit", "Owl", "DeerSilhouette", "WhaleTail", "WindowGlow", "Tent", "Lantern", "Sailboat"]))
    var focal: String

    @Guide(description: "One small extra touch in the air",
           .anyOf(["None", "Cloud", "MistBand", "Aurora", "Snowfall", "FireflyScatter", "BirdFlock", "StarCluster"]))
    var accent: String

    @Guide(description: "The mood", .anyOf(["calm", "dreamy", "cozy", "wonder"]))
    var mood: String
}
