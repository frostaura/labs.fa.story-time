# Procedural parallax poster engine

Every story has a poster: four parallax layers that shift with device tilt on the Home
card, the Kid Shelf tile and the player. The founder ruled out bundled and cloud image
models (2026-10-14, see [`../research/poster-imagery.md`](../research/poster-imagery.md)),
so posters are entirely procedural. The engine lives in `src/app/StoryTime.Poster`
(net10.0 + SkiaSharp, no MAUI). It renders headless on the Mac for tests, goldens and
contact sheets, and the MAUI head references it for on-device rendering.

## Spec contract for the story engine

The story engine emits only this. It is enum-only: no coordinates, no sizes, no colours,
no drawing instructions. Everything visual is decided by the engine from the spec and the
seed. The contract is `PosterSpec` in `src/shared/StoryTime.Contracts/Models.cs`, contract
version **3**.

| Field | Type | Values |
|---|---|---|
| `Version` | int | `3` (written by the engine; readers ignore it) |
| `Scene` | `PosterScene` | `MoonlitSea`, `ForestClearing`, `MountainLake`, `FireflyMeadow`, `SnowyVillage`, `DesertOasis`, `IslandCove`, `StarryHills`, `ReefAtDusk`, `CloudKingdom`, `TownRooftops`, `JungleCanopy` |
| `TimeOfDay` | `PosterTimeOfDay` | `Dusk`, `Twilight`, `Moonlit`, `Starlit` |
| `Mood` | `PosterMood` | `Calm`, `Cozy`, `Dreamy`, `Magical`, `Sleepy`, `Curious` |
| `Palette` | `PosterPalette` | `NightHarbor`, `MoonMeadow`, `OceanDeep`, `PlumDusk`, `PineForest`, `CloudDrift`, `StarfieldViolet`, `LanternGlow`, `TidePool`, `SnowHush`, `EmberSky`, `MistValley` |
| `Features` | list of `PosterFeature`, 0–3, most important first | see the next table |
| `Seed` | int | any; drives all variation (ridges, stars, sides, poses) |

**JSON.** Enums are written by name. They are read leniently: any case, or an integer.
Unknown values become undefined and are then repaired, so the parse never throws.
Example:

```json
{"Version":3,"Scene":"MoonlitSea","TimeOfDay":"Moonlit","Mood":"Calm","Palette":"NightHarbor","Features":["Lighthouse","Sailboat"],"Seed":101}
```

**Repair rules** (`PosterSpecRules.Normalize`). These are deterministic and idempotent.

- An undefined scene comes from the palette.
- An undefined palette is the scene's default.
- An undefined time becomes `Moonlit`; an undefined mood becomes `Calm`.
- Features are de-duplicated and capped at 3.
- Any feature not compatible with the scene is **dropped**, never moved somewhere else.
- At most one creature (the first) is kept. It becomes the focal subject.
- `MilkyWay` with `Moonlit` or `Dusk` becomes `Starlit`.
- `Aurora` at `Dusk` becomes `Twilight`.
- `ReefAtDusk` at `Starlit` becomes `Twilight`.

**Feature → allowed scenes** (`PosterSpecRules.Compatibility`). A guided-generation schema
should offer only the compatible subset per scene; the engine enforces it either way.

| Feature | Scenes |
|---|---|
| `Lighthouse` | MoonlitSea, IslandCove |
| `Cabin` | ForestClearing, MountainLake, SnowyVillage, FireflyMeadow, StarryHills |
| `Village` | SnowyVillage, TownRooftops, FireflyMeadow, MountainLake, StarryHills, MoonlitSea |
| `Castle` | CloudKingdom, StarryHills, MountainLake, FireflyMeadow, SnowyVillage, TownRooftops |
| `Windmill` | FireflyMeadow, StarryHills |
| `Tent` | DesertOasis, StarryHills, ForestClearing, MountainLake, FireflyMeadow |
| `Lantern` | every land scene, IslandCove, MoonlitSea |
| `Boat` | MoonlitSea, MountainLake, IslandCove |
| `Sailboat` | MoonlitSea, IslandCove |
| `Balloon` | CloudKingdom, StarryHills, FireflyMeadow, MountainLake, DesertOasis, TownRooftops |
| `Train` | FireflyMeadow, StarryHills, MountainLake, SnowyVillage |
| `Telescope` | StarryHills, TownRooftops, DesertOasis |
| `Fox` | ForestClearing, FireflyMeadow, SnowyVillage, StarryHills, MountainLake, MoonlitSea |
| `Rabbit` | FireflyMeadow, ForestClearing, StarryHills, SnowyVillage |
| `Owl` | ForestClearing, JungleCanopy, SnowyVillage, FireflyMeadow, MountainLake |
| `Deer` | ForestClearing, MountainLake, FireflyMeadow, SnowyVillage |
| `Bear` | ForestClearing, MountainLake, StarryHills |
| `Cat` | TownRooftops, SnowyVillage |
| `Hedgehog` | ForestClearing, FireflyMeadow |
| `Otter` | MoonlitSea, MountainLake, IslandCove |
| `Whale` | MoonlitSea, IslandCove, ReefAtDusk |
| `Turtle` | ReefAtDusk, IslandCove |
| `Fish` | ReefAtDusk |
| `Camel` | DesertOasis |
| `Dinosaur` | FireflyMeadow, JungleCanopy, StarryHills, MountainLake (a gentle long-neck grazer) |
| `Birds` | every scene except ReefAtDusk |
| `Fireflies` | ForestClearing, FireflyMeadow, JungleCanopy, MountainLake, StarryHills, DesertOasis |
| `Flowers` | FireflyMeadow, ForestClearing, StarryHills, MountainLake, JungleCanopy |
| `Mushrooms` | ForestClearing, FireflyMeadow, JungleCanopy |
| `ShootingStar` | every scene except ReefAtDusk (use rarely: the occasional delight, not a fixture) |
| `Aurora` | SnowyVillage, MountainLake, StarryHills, MoonlitSea, ForestClearing, CloudKingdom |
| `MilkyWay` | every scene except ReefAtDusk |
| `Clouds` | every scene except ReefAtDusk |
| `Snowfall` | SnowyVillage, MountainLake, ForestClearing, TownRooftops |

**Without a model.** `PosterSpecDeriver.Derive(outline)` derives a spec deterministically
from the outline's title, settings, character kinds and moral. It uses word-aware keyword
cues scored per scene, boosts scenes compatible with the story's creature, and samples
among scenes within 60% of the best score (weighted by score²). Features come from the
story first, then seeded scene extras. `TemplateStoryEngine` uses it, and any engine can
fall back to it.

**Stored specs.** `PosterSpecJson.Parse` reads v3 and also upgrades the retired v2
formation contract (palette + free-string mood + layers of positioned shapes). The
palette, seed and mood survive, the scene comes from the palette and motifs, and
recognisable shapes become features. Every story already on a device keeps a poster.

**Reconciling the Foundation Models lane.** The `@Generable` poster type should mirror
the table above: five enums, plus a feature array of at most 3. Map its output straight
into `PosterSpec`, set `Seed` from the outline seed, and call `PosterSpecRules.Normalize`.
Prompting should describe scenes, not drawing.
`StoryPrompts.PosterUser` in this branch still describes the retired formation contract
and is owned by that lane.

## Architecture

```
PosterSpec ─► PosterSpecRules.Normalize ─► ScenePlanner.Plan ─► PosterPlan (pure data)
                                                │
          ScenePalette.Build (OKLCH) ◄──────────┘
                                                ▼
                 PosterEngine.RenderLayers(plan, w, h, travelScale, bakeAmbient)
     ┌──────────────┬───────────────┬───────────────┬───────────────┬────────────┐
     backdrop        depth 0         depth 1         depth 2         depth 3      vignette
     (fixed sky)     halo, Milky Way far bands,      mid bands,      near bands,  (fixed)
                     aurora, stars,  water, far      structures,     focal subject,
                     clouds, moon    props           trees           framing
                                                ▼
     ParallaxPosterView (app): Images translated by TiltMotion × layer travel;
     ambient particles (twinkle, fireflies, glints, snow, bubbles) on a 30 fps canvas
```

- **Plan first, pixels second.** `ScenePlanner` turns the spec into a `PosterPlan` before
  anything is painted. The plan holds:
  - the horizon, the moon, every star, the clouds;
  - terrain bands, as deterministic 1D profiles;
  - every prop's anchor, size and pose;
  - the ambient particles and the title-safe rectangle.

  Tests check composition rules against the plan without rendering it. Every random choice
  comes from a labelled fork of the seed (`Rng.Fork("label")`), so changing how one element
  is built never reshuffles another.
- **Coordinates.** Plan coordinates are normalised to the visible frame. The overscan
  bleed is just x < 0 or x > 1, and sizes are fractions of the shorter side. Each terrain
  profile is sampled across the full bitmap, bleed included, and filled to the bitmap's
  bottom edge.
- **Layers** are cropped to their painted content. Each one carries its crop rectangle and
  its travel. The backdrop never moves, so it can never reveal an edge, and the stars and
  moon travel against it.
- **Seams.**
  - `Shared.TiltMotion` reference-counts the single accelerometer for every poster on
    screen, low-pass filters once, and broadcasts the result.
  - `PosterLayerCache` renders off the main thread, de-duplicates in-flight renders, and
    keeps PNG layers in a 48 MB memory LRU over a disk cache keyed by ArtVersion. Scrolling
    back or reopening costs a decode, not a render.
  - `IPosterComposer.Validate` maps to `PosterSpecRules`.
  - `IPosterRasterizer` produces flattened thumbnails through `PosterEngine.RenderFlattened`.
- **Cards versus player.**
  - Player: full travel, device density, live ambient.
  - Home cards: travel × 0.5, rendered at 2×, ambient baked at t = 0.
  - Kid Shelf: travel × 0.45, rendered at 2×, ambient baked at t = 0.
  - Under Reduce Motion: no tilt, and ambient baked.
  - The flattened thumbnail sits underneath, so a card is never blank while its layers
    load.
- **Versioning.** `PosterEngineInfo.ArtVersion` (now 9) stamps thumbnail names and cache
  keys.

### Scene vocabulary

| Scene | Far (1) | Mid (2) | Near (3) |
|---|---|---|---|
| MoonlitSea | sea plane, headland, distant isle, moonglade, reflections | rocky point or lighthouse cliff; boats, whale or otter on the water | dune shore, grass, reeds |
| ForestClearing | hazy ridge, small-pine treeline | meadow, tall pines framing a clearing, cabin | grass, fern, big framing pine |
| MountainLake | two ridged ranges with snow caps, pine line, lake with mirrored ranges | pine shore on one side | rocks, reeds |
| FireflyMeadow | two rolling hill bands | hill with a lone round tree | tall grass, bush; fireflies |
| SnowyVillage | snowy range | snow slope, 3–5 lit houses, snowy pines | drift, fence run, framing snowy pine; snowfall |
| DesertOasis | two dune bands | oasis pool with palms | near dune |
| IslandCove | sea plane, far islet | palm island | beach, framing palm |
| StarryHills | two hill bands | hill with a lone tree (or tent, telescope) | dark hill, grass; often the Milky Way |
| ReefAtDusk | reef mounds in blue haze; light shafts above | coral mounds, branch and fan corals | kelp, coral; bubbles and motes |
| CloudKingdom | far cloud bank | big cloud bank | billowing near clouds |
| TownRooftops | skyline (one dome at most), tiny lit windows | rooftops, chimneys, lit windows | roof ridge, chimney with smoke |
| JungleCanopy | crown line in mist | crowns, a framing trunk, vines | fans of broad leaves, fern |

These combine with 4 times of day, 6 moods, 12 palettes, 34 features and per-seed
variation:
- side (focal on the left or right third), horizon jitter, ridge noise;
- moon phase and position, star field, cloud count;
- tree, house and palm counts and sizes;
- creature pose (a fox sits or curls asleep).

Composition rules, enforced by the planner and checked by tests:
- The horizon sits on or below the lower third.
- The focal subject stands on a vertical third, and the moon takes the opposite third.
- The moon stays out of the title band and off the horizon.
- Clouds and sparkles stay out of the title band.
- Framing silhouettes go at the edges, never the centre.
- There is one warm light family (amber), small and few.

## Research highlights (Phase 1)

**Layered ridges and depth**
- 1D fBm with gain 0.5 (H = 1, the roughness Quilez measured on real mountain silhouettes)
  and lacunarity detuned to 2.01, so octave peaks never stack ([Quilez, fBm](https://iquilezles.org/articles/fbm/)).
- A 1/f spectrum reads as long hills ([Red Blob Games, noise](https://www.redblobgames.com/articles/noise/introduction.html)).
- Ridged multifractal (offset − |n|)² with previous-octave weighting, blended with fBm so
  peaks are not jagged. This is used for the far ranges only.
- Aerial perspective: distant forms are lighter, lower in contrast and saturation, and
  closer to the horizon colour. The engine mixes each band toward the horizon haze in
  OKLab by its depth.
- Valley fog: a gradient at each band's foot, in the style of Quilez's height fog
  ([fog](https://iquilezles.org/articles/fog/)).
- Firewatch's art direction uses flat colour layers, strong silhouettes and fog for depth,
  with the sky as the largest colour source ([GDC 2015 summary](https://www.thumbsticks.com/gdc-2015-the-art-of-firewatch)).

**Gouache feel**
- Subtle is the rule: grain kept within a few percent, low-frequency value blotches, and
  crisp near layers ([Tyler Hobbs, simulating watercolour](https://www.tylerxhobbs.com/words/a-guide-to-simulating-watercolor-paint-with-generative-art)).
- Implementation: a deterministic 384² tile of 70% blotch and 30% tooth. It is laid on
  each layer with SrcATop at 2.6–4.4% opacity, so the sky stays clean and silhouettes gain
  paint.

**Banding**
- Dither last, just before quantising.
- Use triangular-PDF noise of about ±1 LSB: white noise at one step leaves bands, and
  triangular noise fixes them
  ([Wronski](https://bartwronski.com/2016/10/30/dithering-part-three-real-world-2d-quantization-dithering/);
  [Blender's dither change](https://archive.blender.org/lists/bf-blender-cvs/2020-February/135857.html)).
- Implementation: the sky ramp and the moon halo are computed per pixel in float, encoded
  through a linear→sRGB LUT, then TPDF-dithered. Small glows use Skia's dither.

**Stars**
- Bridson Poisson-disc sampling: k = 30, an annulus of [r, 2r], grid cell r/√2
  ([Bridson 2007](https://www.cs.ubc.ca/~rbridson/docs/bridson-siggraph07-poissondisk.pdf)).
- Magnitude classes give about 3× more stars per magnitude, and each magnitude is 2.512×
  dimmer ([Sky & Telescope](https://skyandtelescope.org/astronomy-resources/how-many-stars-night-sky-09172014/)).
- Extinction toward the horizon, wash-out near the moon, and a hush in the title band.
- Tints: 70% cream, 15% pale blue, 15% warm.
- Twinkle is opacity-only, has a 2–6 s period, and applies to 10–20% of bright stars.

**Water**
- The glitter path is as wide as the moon on calm water and widens toward the viewer
  ([NOAA PSL](https://psl.noaa.gov/outreach/education/science/glitter);
  [Atmospheric Optics](https://atoptics.co.uk/fz236.htm)).
- Reflections are mirrored, darkened toward deep water and broken by perspective-spaced
  ripple gaps.

**Colour**
- OKLab/OKLCH for every palette and mix: perceptual, and free of the purple drift that
  CIELAB and HSV show ([Ottosson, OKLab](https://bottosson.github.io/posts/oklab/)).
- Gamut mapping reduces chroma at constant L and hue ([gamut clipping](https://bottosson.github.io/posts/gamutclipping/)).
- Contrast uses WCAG 2.x: 4.5:1 for text and 3:1 for large text ([WCAG 1.4.3](https://www.w3.org/WAI/WCAG21/Understanding/contrast-minimum.html)).

**Picture-book principles**
- Values darken as bedtime approaches, with few and small warm lights ([Kirkus on *Goodnight Moon*](https://kirkusreviews.com/book-reviews/margaret-wise-brown/goodnight-moon)).
- Limited palettes, with strong colour saved for emphasis, keep things "a little calm"
  ([Klassen interview](https://bestfriendsarebooks.com/2012/10/10/this-is-not-my-hat-blog-tour-interview-with-jon-klassen-giveaway/)).
- Covers give the title a dominant, uncluttered area ([SFWA on cover design](https://sfwa.org/2026/03/10/improving-your-covers-with-graphic-design-theory/)).
- These are style principles only; nothing is copied.

**Parallax**
- Apple's motion-effect sample moves the foreground ±15 pt and the recessed background
  ±25–32 pt, with views larger than their parent ([MotionEffects sample](https://developer.apple.com/library/archive/samplecode/sc1249/Listings/MotionEffects_BasicInterpolationVC_m.html)).
- We keep the founder's device-calibrated travel of [12, 20, 30, 40] pt on a 226 pt
  poster, because lower values read as static on an iPhone (2026-07-31). Bleed is the
  deepest travel plus 2 px.
- Reduce Motion disables parallax ([Apple Support](https://support.apple.com/en-us/HT202655)).
- Parallax counts as non-essential motion under [WCAG 2.3.3](https://www.w3.org/WAI/WCAG21/Understanding/animation-from-interactions.html).
- Drift has periods of 7 s or more; opacity changes have periods of 2 s or more.

**Performance**
- Core Animation caches layer bitmaps, so moving a layer is GPU compositing with no CPU
  redraw ([Core Animation basics](https://developer.apple.com/library/archive/documentation/Cocoa/Conceptual/CoreAnimation_guide/CoreAnimationBasics/CoreAnimationBasics.html)).
- `SKGLView` on iOS is OpenGL ES, which is deprecated ([docs](https://learn.microsoft.com/en-us/dotnet/api/skiasharp.views.maui.controls.skglview)).
  The engine therefore renders once with a raster `SKSurface`. Per-frame Skia draws only
  the few ambient dots, at 30 fps.
- ProMotion rates are 120 Hz and its divisors ([WWDC21 10147](https://developer.apple.com/videos/play/wwdc2021/10147/)).
  The tilt arrives at about 50–60 Hz, so the compositor needs no 120 Hz.

## Test inventory

The tests live in `tests/StoryTime.Poster.Tests`. Measured numbers are written to
`/private/tmp/storytime-posters/*.txt` on every run.

- **Spec contract**
  - fuzzed `Normalize` (3 000 garbage specs): valid and idempotent;
  - drop-not-relocate;
  - light repairs;
  - lenient JSON by name;
  - v2 legacy upgrade;
  - deterministic, word-aware derivation;
  - this document lists every enum value.
- **Render**
  - identical pixels for the same spec, and different pixels for a different seed (layers
    too);
  - every scene × palette renders an opaque, non-blank, non-blown frame;
  - 500 plans with finite, in-frame geometry;
  - composition rules;
  - every silhouette family paints pixels;
  - premultiplied-alpha sanity and non-empty layers;
  - max-tilt overscan at nine extremes per scene, with no edge, seam or gap visible against
    a sentinel;
  - title-band contrast for cream text;
  - banding: no identical-value run over 14 px and no staircase in the 16 px means, with an
    undithered control that does band;
  - ambient animates, and the still bakes it.
- **Palette**
  - exact Hearth anchors;
  - dark, ordered, calm skies;
  - atmospheric depth ordering;
  - analogous families;
  - gamut;
  - an OKLab round trip.
- **Variety**: pHash plus mean-OKLab "alike" rate across 100 consecutive nights for one
  child, and across 300 random specs.
- **Golden**: 12 fixed posters, one per scene, under
  `tests/StoryTime.Poster.Tests/golden/`. To approve a change, run
  `golden/regenerate.sh`, review the PNGs, and bump ArtVersion.
- **Performance**: the player layer set at iPhone Air 3× (678 px plus bleed) has a median
  budget of 250 ms on the Mac; memory budgets are 16 MB decoded and 3 MB PNG; thumbnails
  have a 120 ms budget; card timing is measured.

`tools/StoryTime.PosterLab` renders the contact sheets and parallax previews to
`/private/tmp/storytime-posters/`:
- `dotnet run -c Release`;
- `focus <Scene> [n]` for larger inspection sheets;
- `layers <Scene>` for a per-layer dump.

## Visual critique log

Each round has three independent reviewers:
1. a children's-book illustrator / art director;
2. colour and composition;
3. technical artifacts.

Each reviews the contact sheets and parallax frames. The loop stops after two consecutive
rounds with no MEDIUM-or-worse defect, or after five rounds.
