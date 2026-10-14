#!/usr/bin/env bash
# Mac end-to-end lab: compiles the shim sources + the lab host for macOS (FoundationModels is
# on macOS 27 too), then runs the REAL C# AppleFoundationStoryEngine (outline → scenes →
# digests → polish → poster) against it through a process-pipe bridge.
#   src/native/lab/run-fm-lab.sh [sceneCount]
# Output (timings + the story) goes to .tmp/fm-lab/ at the repo root.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
OUT="$ROOT/.tmp/fm-lab"
mkdir -p "$OUT"

xcrun swiftc -O -swift-version 6 -warnings-as-errors -target arm64-apple-macos27.0 \
  "$HERE/../StoryTimeNative/Sources/FoundationModels/"*.swift "$HERE/FMLabHost/main.swift" \
  -o "$OUT/fmlabhost"

source "$ROOT/src/build-env.sh"
dotnet run --project "$HERE/FmLab.Console/FmLab.Console.csproj" -- "$OUT/fmlabhost" "$OUT" "${1:-3}"
