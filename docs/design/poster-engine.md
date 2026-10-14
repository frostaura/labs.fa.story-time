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
- **Versioning.** `PosterEngineInfo.ArtVersion` (now 10) stamps thumbnail names and cache
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
- creature pose (a fawn stands or lies folded);
- layout: **Third** (hero on a third, moon on the other), **Center** (hero centred and
  seen from low, moon over a shoulder) or **High** (a raised vantage, horizon 0.08 higher).
  Reef, cloud kingdom, jungle and cove use Third and Center.

### Picture-book pass (pass 2)

The pass-1 engine read as dark wallpaper. Pass 2 sets a picture-book cover bar.

- **Every cover has a character.** A listed creature is the hero. When the story lists
  none, a scene-native **companion** creature becomes the hero, rotated evenly by seed: an
  otter, fox, deer, bear, cat, turtle, dinosaur and so on. Companions are scenery, not
  features, so the spec stays exactly what was asked. The scene's home prop (boat, cabin,
  tent, castle, snowman, toadstools) still appears, smaller, on the other third.
- **Hero size.** Heroes are sized by what is actually drawn
  (`PropPainter.HeightFactor`), to 25–32% of the poster height. They are kept below the
  title band, and stay inside the frame at full tilt (`FitHeroInFrame`).
- **Creature sheet** (`PropPainter.Creatures.cs`). Every creature shares one shape
  language:
  - a big head on a small, soft body;
  - a sleepy, kind face (smiling closed eyes; open eyes only on Curious nights), a
    button nose and a blush;
  - local colour graded into the night (`ScenePalette.Grade`);
  - a form gradient, a moon rim, a warm wash and rim on the side facing its light, and a
    soft ink contour;
  - a dark contact pool behind the hero, so it separates on snow and in water.
  - A dinosaur or turtle switches to ochre or amber-olive when its ground would swallow it.
- **Structures** are in local colour: timber cabins, painted roofs, stone castles with
  coloured caps, red or blue hulls, cream sails, bell tents and a brass telescope.
- **Warm focal light.** Every poster carries one dominant warm light beside its hero: a
  lit cabin or tent, a campfire, a lantern, or a glowing anemone on the reef.
  - The light's pool (`LightPainter`) washes the ground and props of its own layer.
  - The light keeps a clear gap from the hero (`KeepLightsOffHero`), and stands on solid
    ground only.
  - Campfires burn on a bare patch.
  - The hero turns to face its light.
- **Foreground framing** (`FrameCluster`). Each scene family has its own kit, rising in
  both lower corners: full on the open side, and compressed beside the hero.
  - meadow: leaves, grass and flowers;
  - forest: ferns and toadstools;
  - snow: a snowy fir, a drift and berries;
  - shore: rocks and cattails;
  - beach: rocks, fronds and a shell;
  - desert: a dune and a rosette;
  - reef: kelp and coral;
  - cloud: puffs;
  - town: a tiled roof corner with a lit dormer, and a window box beside the hero;
  - jungle: leaves.
- **Hero clear zone.** Landmarks and trees behind the hero step to its far side, and
  only onto solid ground. Kelp, leaves and bushes that would slice through the hero
  step out.
- **Palettes are colour scripts.** Each has a sky hue, a glow hue and a land hue:
  amber, rose and plum dusks, teal, moss, sunset gold and lilac, alongside the blues.
  - The seed nudges sky and glow hues within the family.
  - The sky ramp runs zenith, mid, low, horizon through a monotone cubic in LCH, so a dusk
    blushes through rose instead of greying.
  - The land keeps local colour a step below the sky.
  - Town roofs go cool under a warm sky.
  - The moon is a warm ivory, dimmer than the lantern core.

Composition rules, enforced by the planner and checked by tests:
- The horizon sits on or below the lower third.
- The hero stands on a vertical third (or is centred in the Center layout). A wide hero
  may sit nearer the centre to stay in frame. The moon takes the other side.
- The moon stays out of the title band and off the horizon.
- Clouds, sparkles and the hero stay out of the title band.
- Framing silhouettes go at the edges, never the centre.
- There is one dominant warm light, beside the hero.

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
`/private/tmp/storytime-posters-v2/*.txt` on every run, or to `$STORYTIME_REPORT_DIR`
when it is set. The assembly runs its classes serially, so the timing budgets measure the
engine and not the test load.

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
  - analogous families, and every palette its own colour script (sky and glow sectors);
  - gamut;
  - an OKLab round trip.
- **Variety**: pHash plus mean-OKLab "alike" rate across 100 consecutive nights for one
  child, and across 300 random specs.
- **Art direction** (`ArtDirectionTests`, pass 2):
  - the hero's visible height (a render-with/without mask, so occlusion counts) is
    0.20–0.38 of the poster;
  - a warm focal glow is present (warm hue, ≥0.2% of the area, ≥0.30 L above the median);
  - foreground framing covers ≥30% of one lower corner and ≥5% of the other;
  - palette range across 300 posters: hue entropy, mean OKLab distance, navy/violet share
    and within-poster value range;
  - every listed feature is visible at card size (160 px, ≥0.25% of the area at ΔE > 0.05).
- **Golden**: 12 fixed posters, one per scene, under
  `tests/StoryTime.Poster.Tests/golden/`. To approve a change, run
  `golden/regenerate.sh`, review the PNGs, and bump ArtVersion.
- **Performance**: the player layer set at iPhone Air 3× (678 px plus bleed) has a median
  budget of 250 ms on the Mac; memory budgets are 16 MB decoded and 3 MB PNG; thumbnails
  have a 120 ms budget; card timing is measured.

`tools/StoryTime.PosterLab` renders to `/private/tmp/storytime-posters-v2/`:
- `dotnet run -c Release`: 60 posters on three contact sheets, a card-size sheet, and
  3-frame parallax previews for 8;
- `creatures`: every creature at player and card size on three nights;
- `one <n>`: sheet poster #n at 700 px, with its placements printed;
- `rand <i>`: the test kit's random spec #i, for a poster a metric flagged;
- `perf`: render time with and without props, framing, creatures and lights;
- `focus <Scene> [n]` and `layers <Scene>`.

## Visual critique log

Each round has three independent reviewers:
1. a children's-book illustrator / art director;
2. colour and composition;
3. technical artifacts.

Each reviews the contact sheets and parallax frames. The loop stops after two consecutive
rounds with no MEDIUM-or-worse defect, or after five rounds.

### Round 1 (first full build)

Twelve HIGH defects were reported across the three lenses.

- **Broken silhouettes**
  - The cat drew only its tail, the deer only its antlers, and the camel only its tail.
    Root cause: `SKPaint.GetFillPath` replaces its destination path. `AddStroke` now appends
    instead, and a test asserts that solid silhouettes have a body.
  - The whale read as a speech bubble; it is now a humpback.
  - The owl had glowing eyes; it now has sleepy closed eyes and sits on a real trunk.
  - The desert palms formed a cage; they now root together and fan apart.
- **Placement**
  - Animals stood on water and a tent sat on the lake; placement now checks for solid
    ground.
  - The train floated on a ridge; it now runs on mid-ground rails.
  - The windmill was hidden by a tree; structures now clear default trees.
- **Layering**
  - Twinkling stars were drawn over mountains; ambient particles now draw per layer, in
    both the engine and the app.
  - The moonglade drifted off the moon; the moon now rides the water's layer.
  - Shooting stars crossed in front of clouds; they now draw behind them.
- **No focal subject.** Many posters had nothing to look at; the planner now supplies a
  default subject per scene.
- **JungleCanopy read as a broken crop.** Rebuilt as leaf fans, a framing trunk and a
  crown line.

MEDIUM fixes:
- the moon is full or crescent only and stays clear of ridges;
- quieter title band;
- horizons lowered to 0.64–0.71;
- taller near bands, so a vertical tilt never squeezes them;
- framing pieces kept at least 40% in frame;
- camp lanterns replace street lamps in wild scenes;
- fewer lit windows;
- EmberSky moved to plum, and LanternGlow given a cool sky for amber contrast;
- opaque fan coral and larger fish;
- lifted, tinted near ink.

Reviewer suggestions not taken:
- **Cut vertical travel to 25–40%.** Founder device evidence from 2026-07-31 says lower
  travel reads as static. Taller near bands handle the squeeze instead.
- **Reduce near travel to 4–5%.** Same reason.
- **Add a title scrim.** The measured title-band contrast already passes (worst mean
  5.1:1).

### Round 2

HIGH defects remained:
- **Faceless creatures.** Every creature was a black cut-out. Each now has sleepy closed
  eyes, a lighter chest or belly patch, and a rim light.
- **Stick-figure deer.** Redrawn as a soft doe; small rounded antlers appear only sometimes.
- **The reef scallop read as a sunken balloon.** Replaced by a branching gorgonian fan.
  Reef chroma is capped, coral is dusty rose and kelp is sea green.
- **The title band was still busy.** Now 75% fewer stars there, a brightness cap, and the
  Milky Way fades by 90% in the band.
- **The moon slid behind near-layer framing trunks.** Every full-height framer is now a
  moon exclusion zone, padded by parallax travel. The jungle trunk moved to the focal
  side.
- **Subjects overlapped** (otter over sailboat, fish through turtle). A collision pass now
  pushes subjects apart, inflated by their layers' travel difference, and re-seats them on
  solid ground.
- **Stars showed through clouds.** Cloud lobes are unioned into one opaque outline with a
  bowed underside, and twinkles avoid clouds.
- **Grounded heroes left the frame at a vertical tilt extreme.** Vertical travel is now
  0.6× horizontal in the app and the previews. This is a deliberate partial deviation from
  "full travel on both axes": the founder's 2026-07-31 complaint was a *pinned* vertical
  channel, and 0.6 keeps it clearly live.

MEDIUM fixes:
- a guaranteed small amber light on every poster above water;
- darker timber houses and a darker snow field;
- a cloud-kingdom value ladder that darkens toward the viewer;
- an opaque castle with moonlit turret caps;
- a warm grade into the Hearth page across the bottom 9%;
- crescents open toward the sun at dusk and twilight;
- earthshine disc removed;
- multi-part silhouettes unioned so they show no seams;
- lightening grain specks halved;
- palms rooted behind the pool, and dune mid-ground;
- ±3% horizon variation;
- Sleepy and Cozy moods more visible;
- a smaller cat;
- a rounder cove island.

### Round 3

All three lenses agreed on the top issue.

- **Subjects and the warm light left the frame at a tilt extreme.** The founder's spread of
  [12, 20, 30, 40] pt moved every layer the same way: a 40 pt camera pan with depth on top.
  Travel is now **pivoted between far and mid**, at [−12, −4, +6, +16] pt. Depth comes from
  the differences between layers, and those are preserved: sky against foreground is still
  28 pt. The subject now barely moves, the sky visibly drifts the other way, and bleed
  (and memory) roughly halves.
- **Unsupported props.**
  - The owl's tree is now a slim trunk kept inside the frame.
  - The town telescope stands on the near roof.
  - In the cloud kingdom, castles rise out of a skirt of cloud and take the focal third.
  - Oasis palms stand on the bank beside the pool.
- **Reef values.** The surface is no lighter than L 0.30 and its light is cool; the
  seabed is lifted and less hazy.
- **Cloud kingdom values.** They ran backwards: the ladder is now L 0.47 → 0.22 toward the
  viewer.
- **The moon outranked the subject.** It is smaller (diameter 7–10%), sits at y ≥ 0.36,
  and is a hair dimmer than the title cream. Subjects are 10–20% larger.
- **The lantern was a stamp.** It now joins only Cozy nights and about a third of the rest.

MEDIUM fixes:
- EmberSky retuned to a low-chroma rust;
- the oasis glint is one soft highlight, not a ladder;
- amber mushrooms;
- snow capped at 0.7 alpha, softer in front and dim behind the title;
- framing pieces present 60% of the time;
- a featured landmark becomes the subject (no duplicate castle).

### Round 4

HIGH defects remained:
- **Creature drawing.** The deer read as a llama, the rabbit was oversized and staring, the
  cat read as the Halloween icon, and the otter was a blob. All four are redrawn:
  - a fawn with a teardrop body, hock bends, spots and a grazing pose half the time;
  - a crouched loaf rabbit with a cream tail;
  - a sleepy loaf cat with its tail wrapped round;
  - a belly-up otter with a muzzle, paws and a shell.
- **The oasis pool read as a pit.** It now mirrors the sky, is always lighter than the
  sand, and has no hard rim.
- **The title band.** No Milky Way stars inside it, the haze fully suppressed there, and a
  −0.03 L scrim at the top of the sky.
- **Reef surface wash.** It now keeps the water's own hue.
- **Props in each other.** Big framing trees step out of the picture when they crowd a
  subject. Footprints must sit fully on solid ground, so no tent overhangs water.

MEDIUM fixes:
- band crests have a soft blurred lip, not a 1 px outline;
- snow in three depth tiers, mostly tiny and far;
- a soft Milky Way band under the specks, with fewer of them;
- shooting stars kept clear of clouds;
- vertical travel 0.5× horizontal (three reviewers asked for ≤ ⅓; 0.5 keeps the pitch
  channel alive per the founder);
- lighter, always-lit castles in the clouds;
- hand-cut edge wobble on silhouettes;
- a fixed paper tooth.

### Round 5 (final allowed round)

The reviewers still reported MEDIUM-or-worse defects. **The stop condition (two
consecutive quiet rounds) was not met within the five-round cap.**

Fixed after round 5:
- **Lake reflections read as pale "ghost hills".** They now fade out with depth below the
  waterline (DstIn gradient) instead of ending in a hard mirrored edge.
- **IslandCove crowding.** The island and its palms moved to the subject's third, leaving
  the moon open sky. Palms grow only on the island itself. The framing palm is smaller,
  rarer and on the far side.
- **Band-top lips read as seams.** They are now directional: strongest toward the moon,
  near zero away from it.
- **The frame paper tooth mottled the sky.** It is finer and half the strength.
- Starlit star budget reduced by 22%.
- Twinkles keep a wider berth from clouds.
- Brighter snow caps on roofs.
- More gaps in far tree lines.

Defects still open, on reviewer consensus:
- **Creature drawing quality.** The rabbit's ears, the grazing fawn pose and the land turtle
  are still weak in places. Hand-authored paths are the ceiling of this approach; the next
  step is a dedicated creature pass with an illustrator's reference sheet.
- **Within-scene layout sameness.** Each scene has one archetype, with mirroring, jitter
  and optional framers. Reviewers want 2–3 archetypes per scene.
- **Reef assets** (coral variety, kelp ribbons) and **jungle foliage** still read the
  most procedural.
- **Palette spread.** Many palettes land in navy or violet; reviewers ask for warmer-dusk
  and green-slate families, and for an even distribution of palettes across consecutive
  posters.
- **Small features.** Birds and the train can be too small to read at card size; a
  post-render visibility check per listed feature is proposed.
- **Vertical tilt.** Reviewers want ≤ ⅓ of horizontal; the engine uses ½, respecting the
  founder's pinned-channel evidence. This needs a founder call on a device.

## Picture-book pass critique log (pass 2)

Same three lenses, run as independent reviewers on every round. The stop rule was two
consecutive rounds with no MEDIUM-or-worse defect, within five rounds.

**The loop did not go dry.** No round was free of MEDIUM defects. HIGH defects fell from 4
to 2.

| Round | Art director | Colour / value | Technical |
|---|---|---|---|
| 1 | 3 H · 5 M · 4 L | 0 H · 6 M · 5 L | 1 H · 10 M · 8 L |
| 2 | 3 H · 7 M · 5 L | 0 H · 9 M · 3 L | 3 H · 5 M · 9 L |
| 3 | 0 H · 8 M · 5 L | 0 H · 6 M · 4 L | 7 H · 6 M · 4 L |
| 4 | 0 H · 6 M · 7 L | 0 H · 7 M · 4 L | 3 H · 4 M · 12 L |
| 5 | 2 H · 7 M · 4 L | 0 H · 7 M · 4 L | 2 H · 7 M · 7 L |

**Fixed along the way:**
- **Round 1:** posters with no character got companion heroes; the campfire was redrawn;
  props stopped standing on water; the moon was dimmed and shrunk; the olive skies were
  removed; the depth ladders were deepened; the town got a roof corner; the boat lost its
  "ladder" reflection; the surfaced whale was fixed; the birds were redrawn.
- **Round 2:** fires on heroes; heroes cropped by the frame; the hedgehog was redesigned;
  kelp sliced through swimmers; fireflies sat on heroes.
- **Round 3:** lights were stuck against heroes, because a too-strict ground limit left
  them nowhere to move; the owl's light went under its branch; a mast grew out of a
  turtle; warm wash on heroes; ember and town palettes.
- **Round 4:** the owl's own crowned tree; higher shores; the rooftop trough; grass in
  fires; lighter coats.
- **Round 5:** the curled fox was retired from rotation after three rounds of reading as
  a loaf (the sitting fox reads well); a telescope stood over the hero; a swimming turtle
  rose toward the title band; a lighthouse could be re-seated below the frame. All four
  were verified fixed by inspection, without a sixth round.

A defect found in passing: the lab's `one <n>` mode drew its random values in a
different order from the contact sheet, so `one N` was not sheet poster #N. It is fixed.

**Still open at the cap (MEDIUM, by reviewer consensus):**
- **Set-level sameness.** Most heroes sit low in the frame, asleep, beside a lantern or a
  fire. The pHash variety mean dropped about 1.6 bits against pass 1 (alike-pair rates are
  flat and within thresholds). The next step is more layout archetypes (close-up, vista,
  over-the-shoulder), a horizon range of 40–70%, and placing light and moon independently.
- **Heroes don't use their props.** No hero holds the lantern, looks through the telescope
  or sits in the boat or tent. Interaction poses per species are the next creature pass.
- **Palette clustering at card size.** About two thirds of the set is teal or navy night,
  because the story engine's palette and time choices drive this. Weighting toward warm
  dusks belongs with the spec deriver and the integration lane, not this engine.
- **Value and temperature separation.** Some heroes still sit close in value to their
  ground (grey cat on snow, olive turtle on teal, a warm fox on a warm sky). Layers merge
  in some night lake, cloud and reef posters.
- **Weak creatures.** The camel, the standing deer, the upright otter and the swimming
  whale read weakest. Fish and birds are props only and are never made the hero.
- **Small features.** Birds, fireflies, aurora and snow pass the visibility metric, but
  reviewers want a recognisability bar at card size.

