import Foundation
import FoundationModels

/// Poster choice: exactly the poster spec contract v3 (docs/design/poster-engine.md,
/// "Spec contract for the story engine") minus the seed, which the C# side derives
/// deterministically from the story. Enum-only: no coordinates, sizes or colours.
/// The C# mapper (StoryTime.Core Engines/AppleFoundation/PosterSpecMapper.cs) parses the
/// names and runs PosterSpecRules.Normalize, which drops any feature that does not fit
/// the scene, so this type and that mapper are the only places a contract change lands.
/// Fields generate in declaration order: the scene is chosen first, everything else fits it.
@Generable
struct SEFMPosterChoice {
    @Guide(description: "The one place this story's picture shows",
           .anyOf(["MoonlitSea", "ForestClearing", "MountainLake", "FireflyMeadow", "SnowyVillage", "DesertOasis",
                   "IslandCove", "StarryHills", "ReefAtDusk", "CloudKingdom", "TownRooftops", "JungleCanopy"]))
    var scene: String

    @Guide(description: "The light in the sky", .anyOf(["Dusk", "Twilight", "Moonlit", "Starlit"]))
    var timeOfDay: String

    @Guide(description: "How the story feels", .anyOf(["Calm", "Cozy", "Dreamy", "Magical", "Sleepy", "Curious"]))
    var mood: String

    @Guide(description: "The colour family that suits the place and the mood",
           .anyOf(["NightHarbor", "MoonMeadow", "OceanDeep", "PlumDusk", "PineForest", "CloudDrift",
                   "StarfieldViolet", "LanternGlow", "TidePool", "SnowHush", "EmberSky", "MistValley"]))
    var palette: String

    @Guide(description: "Up to three things from this story that belong in that place, most important first; at most one animal",
           .maximumCount(3),
           .element(.anyOf(["Lighthouse", "Cabin", "Village", "Castle", "Windmill", "Tent", "Lantern", "Boat", "Sailboat",
                            "Balloon", "Train", "Telescope", "Fox", "Rabbit", "Owl", "Deer", "Bear", "Cat", "Hedgehog",
                            "Otter", "Whale", "Turtle", "Fish", "Camel", "Dinosaur", "Birds", "Fireflies", "Flowers",
                            "Mushrooms", "ShootingStar", "Aurora", "MilkyWay", "Clouds", "Snowfall"])))
    var features: [String]
}
