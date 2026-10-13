<p align="center">
  <img src="README.icon.png" alt="StoryTime" width="300" />
</p>

# StoryTime 🌙

A calm-first, mobile-focused story generation platform with a .NET API, a React PWA frontend, and an iOS 27+ host app powered by on-device Apple Intelligence. Android is not a build target.

## Features
- Quick Generate from Home with always-visible duration slider.
- Series continuity with Story Bible state.
- One-shot generation mode.
- iOS 27+: on-device story drafting with Apple Intelligence (Foundation Models), with no model downloads.
- iOS 27+: native on-device dictation (`SpeechAnalyzer` + `DictationTranscriber`) for Quick Generate fields.
- Parent approval flow (teaser first, full audio on approval).
- Kid Shelf browsing limited to Recent + Favorites.
- Tier-based cooldown, concurrency, and duration enforcement.
- Procedural parallax poster fallback with 3-5 layers.
- Live-backend Playwright coverage for generation, paywall, and strict localhost passkey flows.
- Automated WCAG AA contrast checks for core UI surfaces.

## Project Structure
```
docs/
  specs/
  testing/
src/
  backend/
    StoryTime.Api/
    StoryTime.Api.Tests/
  frontend/
  ios/               # iOS 27+ host (XcodeGen project + StoryTimeKit Swift package)
```

## Prerequisites
- .NET 10 SDK
- Node.js 22+
- npm
- make
- iOS only: macOS with Xcode 27 (iOS 27 SDK) and XcodeGen (`brew install xcodegen`)

## Getting Started
```bash
cd src/frontend
npm install
cd ../..
dotnet restore src/backend/StoryTime.slnx
```

Run backend:
```bash
dotnet run --project src/backend/StoryTime.Api
```

Run frontend:
```bash
cd src/frontend
npm run dev
```

## Quality Gates
```bash
make lint
make build
make test
make verify
```

`make verify` is the canonical local CI-equivalent command. It runs lint, build, traceability, the full test suite, test-governance checks, frontend coverage, env-example validation, backend coverage collection, and the backend coverage threshold check.

Install the Playwright browser once before the first full local verification run:

```bash
cd src/frontend
npx playwright install chromium
```

## iOS App (iOS 27+)
```bash
make ios-test     # StoryTimeKit unit tests (Swift Testing)
make ios-build    # generate src/ios/StoryTime.xcodeproj and build for the simulator
```
The host loads the web app from the `STORYTIME_WEB_APP_URL` build setting (default `http://localhost:5173`). Set `STORYTIME_API_BASE_URL` to point the web app at a specific API. Apple Intelligence drafting needs an Apple Intelligence–eligible device with it enabled; otherwise the API composes the story.

## Docker Full Stack
```bash
cp .env.example .env
docker compose up --build -d
open http://localhost:4173
curl http://localhost:8080/api/home/status
python3 scripts/compose-regression.py --webhook-secret "$STORYTIME_CHECKOUT_WEBHOOK_SHARED_SECRET"
docker compose down
```

For live-provider readiness, set `STORYTIME_POSTER_PROVIDER_ENDPOINT`, `STORYTIME_NARRATION_PROVIDER_ENDPOINT`,
`STORYTIME_AI_PROVIDER_ENDPOINT`, and `STORYTIME_CHECKOUT_PROVIDER_ENDPOINT` to real provider URLs and enable the
matching providers explicitly. Series continuity is currently client-owned in the shipped configuration.

## Documentation
- [System Spec](docs/specs/system_spec.md)
- [Test Spec](docs/specs/test_spec.md)
- [Traceability Matrix](docs/specs/traceability_matrix.md)
- [Testing How-To](docs/testing/how-to-run.md)
- [Use Cases](docs/use-cases.md)

## License
MIT License - see [LICENSE](./LICENSE) for details.
