#!/usr/bin/env bash
# Builds StoryTimeNative.xcframework (iOS device + simulator slices) from source.
#   src/native/build.sh            → src/native/build/StoryTimeNative.xcframework
# Requires Xcode 27+ (iOS 27 FoundationModels SDK; deployment target iOS 27.0) and xcodegen.
# The output directory is gitignored; the binding project runs this automatically when
# the xcframework is missing, so a clean clone builds with plain `dotnet build`.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ_DIR="$HERE/StoryTimeNative"
OUT="$HERE/build"
WORK="$OUT/work"
XCF="$OUT/StoryTimeNative.xcframework"

command -v xcodegen >/dev/null || { echo "build.sh: xcodegen not found (brew install xcodegen)" >&2; exit 1; }

rm -rf "$WORK" "$XCF"
mkdir -p "$WORK"

(cd "$PROJ_DIR" && xcodegen generate --quiet --project "$WORK")

archive() {
  local sdk="$1" dest="$2"
  xcodebuild archive \
    -project "$WORK/StoryTimeNative.xcodeproj" \
    -scheme StoryTimeNative \
    -configuration Release \
    -sdk "$sdk" -destination "$dest" \
    -archivePath "$WORK/StoryTimeNative-$sdk.xcarchive" \
    -derivedDataPath "$WORK/DerivedData" \
    SKIP_INSTALL=NO BUILD_LIBRARY_FOR_DISTRIBUTION=YES \
    -quiet
}

archive iphoneos "generic/platform=iOS"
archive iphonesimulator "generic/platform=iOS Simulator"

xcodebuild -create-xcframework \
  -framework "$WORK/StoryTimeNative-iphoneos.xcarchive/Products/Library/Frameworks/StoryTimeNative.framework" \
  -framework "$WORK/StoryTimeNative-iphonesimulator.xcarchive/Products/Library/Frameworks/StoryTimeNative.framework" \
  -output "$XCF"

# iCloud/Finder xattrs break codesign of the embedding app ("detritus not allowed").
xattr -cr "$XCF" 2>/dev/null || true
rm -rf "$WORK"
echo "build.sh: $XCF"
