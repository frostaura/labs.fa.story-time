#!/usr/bin/env bash
# Approve a deliberate visual change: re-render the 12 golden posters, then REVIEW the
# PNG diff (git diff --stat; open the images) before committing them. Bump
# PosterEngineInfo.ArtVersion in the same commit so device caches re-render.
set -euo pipefail
cd "$(dirname "$0")/../../.."
source src/build-env.sh
STORYTIME_UPDATE_GOLDENS=1 dotnet test tests/StoryTime.Poster.Tests/StoryTime.Poster.Tests.csproj \
  --filter "FullyQualifiedName~GoldenTests" --disable-build-servers
git status --short tests/StoryTime.Poster.Tests/golden
