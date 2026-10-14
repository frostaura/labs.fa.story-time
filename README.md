# StoryTime

The phone that tells your child a bedtime story — a brand-new one, every night, **completely offline**.

StoryTime is a .NET MAUI app (iOS + Android) that generates 5–15-minute kids' bedtime stories entirely on-device: a small bundled language model writes the story scene by scene, a procedural composer builds a 4-layer parallax poster for it, and a bundled neural TTS narrates it in one of three consistent voices. No servers, no accounts, no network. Parental oversight is structural: teaser approval, biometric-locked settings, a browse-only Kid Shelf, and a between-stories cooldown.

- Orientation & rules: [`CLAUDE.md`](CLAUDE.md) · current state: [`MEMORY.md`](MEMORY.md)
- Product spec: [`docs/prd.md`](docs/prd.md) · architecture: [`docs/architecture/overview.md`](docs/architecture/overview.md) · design contract: [`docs/design/mockup-spec.md`](docs/design/mockup-spec.md)
- Build/run: see `CLAUDE.md` §Build / test / run (source `src/build-env.sh` first)

A FrostAura Labs program. Private during incubation.
