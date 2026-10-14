# On-device imagery for parallax posters (research, 2026-10-14)

The founder asked for on-device image generation for the parallax backgrounds. Status: **decision
pending.** Results from the empirical lab go in [`genai-lab.md`](genai-lab.md).

## Findings

- **Image Playground `ImageCreator` is discontinued in iOS 27.** Apple's notice of 2026-06-11 says
  it "will no longer work in iOS 27… or later" ([Apple Developer News](https://developer.apple.com/news/?id=dz9wvq0r)),
  and the docs show its availability as iOS 18.4–27.0. Its replacements are not on-device:
  - **The sheet** (`imagePlaygroundSheet`) runs on Private Cloud Compute, is rate-limited, needs a
    person to drive it, and returns a temporary URL (WWDC26-375).
  - **Foundation Models** on iOS 27 gained image *input*, not generation.
- **There is no Apple-native monocular depth API on iOS.** Spatial-scene generation
  (`ImagePresentationComponent.Spatial3DImage`) is visionOS only.
- **`VNGenerateForegroundInstanceMaskRequest` is a poor layer cutter for scenery.** It masks
  "noticeable objects", not hills or treelines.
- **The only fully on-device route is bundled Core ML models:**

  | Model | Size | Licence | Speed |
  |---|---|---|---|
  | Apple `ml-stable-diffusion`, SD 2.1-base 6-bit palettized | ~1.14 GB | OpenRAIL++ | ~7.9 s at 512² / 20 steps on iPhone 14 Pro Max; peak memory above 2 GB; needs `reduceMemory` and the Increased Memory Limit entitlement |
  | Depth Anything V2 Small (Core ML F16) | 49.8 MB | Apache-2.0 | ~31–34 ms on the Neural Engine |

  The models could ship as Apple-hosted Background Assets: downloaded once at install, offline
  thereafter.

## Candidate architecture (if bundling is approved)

1. Turn the poster spec into a closed-vocabulary prompt. Characters never appear (no faces), and
   the negative prompt excludes text, people and anything scary.
2. Run SD at 512×768 with a seed derived from the story id.
3. Run depth on the result.
4. Use the far ~25% of depth plus a top-of-frame prior as the sky mask. Drop that region, so the
   procedural sky shows through.
5. Cut far/mid/near bands from the depth quantiles, with feathered alpha and dilated back layers.
6. Feed the layers to the existing SkiaSharp parallax renderer.

**Show the procedural poster immediately and crossfade to the generated one.** Fall back to
procedural when any of these is true:

- The models are missing.
- The device is thermally serious or in Low Power Mode.
- Generation takes more than 25 s.
- The app is backgrounded.
- The safety check fails.
- A layer is more than 90% transparent.

The SD safety checker stays on.

## Open questions

- Speed and RAM on the iPhone Air.
- Whether depth slicing works on stylised art.
- OpenRAIL++ terms for a commercial kids' app, and App Store review stance in the Kids category.
