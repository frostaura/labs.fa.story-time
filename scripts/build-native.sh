#!/usr/bin/env bash
# build-native.sh — build llama.cpp native libraries for StoryTime.
#
# PINNED: llama.cpp release tag b10189
#         commit b2f221684fcd898e947a121baeda80f345da3e6b  (checked out & recorded 2026-07-30)
# Upgrades are deliberate, never drive-by (memory/watch.md: llama.cpp API churn).
#
# Source tree is cloned OUTSIDE the iCloud-synced repo (default $HOME/.cache/storytime/llama.cpp,
# override with $STORYTIME_NATIVE_SRC). Outputs land in $STORYTIME_NATIVE_ROOT
# (default $HOME/StoryTimeNative), consumed by Native.props via $(STORYTIME_NATIVE_ROOT).
#
# Usage: scripts/build-native.sh [macos|android|ios|sherpa-ios|headers|all]   (default: all)
#   macos      → osx-arm64/  libllama.dylib + libggml*.dylib (Metal, embedded metallib)
#   android    → android-arm64/ libllama.so + libggml*.so (arm64-v8a, CPU only)
#   ios        → ios/ llama.xcframework (device + simulator, via upstream build-xcframework.sh)
#   sherpa-ios → ios/ sherpa-onnx.xcframework + onnxruntime.xcframework (STATIC, device +
#                simulator) from the pinned sherpa-onnx GitHub release — no local compile.
#   headers    → vendor llama.h + ggml headers into Core/Engines/Llama/vendor/
set -euo pipefail

SHERPA_VERSION="1.13.4"
SHERPA_IOS_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/v${SHERPA_VERSION}/sherpa-onnx-v${SHERPA_VERSION}-ios.tar.bz2"
SHERPA_IOS_SHA="596f33bff80046a52144745745fe54d55e8b23659d92209f5ab7d94c1259fe6d"   # verified 2026-07-30

LLAMA_TAG="b10189"
LLAMA_COMMIT="b2f221684fcd898e947a121baeda80f345da3e6b"
LLAMA_REPO="https://github.com/ggml-org/llama.cpp.git"

SRC="${STORYTIME_NATIVE_SRC:-$HOME/.cache/storytime/llama.cpp}"
OUT="${STORYTIME_NATIVE_ROOT:-$HOME/StoryTimeNative}"
NDK_VERSION="${STORYTIME_NDK_VERSION:-27.1.12297006}"
ANDROID_HOME="${ANDROID_HOME:-$HOME/Library/Android/sdk}"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
JOBS="$(sysctl -n hw.ncpu 2>/dev/null || echo 8)"

COMMON_FLAGS=(
  -DCMAKE_BUILD_TYPE=Release
  -DBUILD_SHARED_LIBS=ON
  -DLLAMA_BUILD_TESTS=OFF
  -DLLAMA_BUILD_EXAMPLES=OFF
  -DLLAMA_BUILD_SERVER=OFF
  -DLLAMA_BUILD_TOOLS=OFF
  -DLLAMA_BUILD_APP=OFF
  -DLLAMA_CURL=OFF
)

ensure_src() {
  if [[ ! -d "$SRC/.git" ]]; then
    mkdir -p "$(dirname "$SRC")"
    git clone "$LLAMA_REPO" "$SRC"
  fi
  git -C "$SRC" fetch --tags --quiet origin "refs/tags/$LLAMA_TAG:refs/tags/$LLAMA_TAG" 2>/dev/null || true
  git -C "$SRC" checkout --quiet "$LLAMA_TAG"
  local head; head=$(git -C "$SRC" rev-parse HEAD)
  if [[ "$head" != "$LLAMA_COMMIT" ]]; then
    echo "FATAL: $SRC @ $LLAMA_TAG resolves to $head, expected $LLAMA_COMMIT" >&2
    exit 1
  fi
  echo "llama.cpp source OK: $SRC @ $LLAMA_TAG ($head)"
}

build_macos() {
  echo "=== macOS arm64 (Metal) ==="
  cmake -S "$SRC" -B "$SRC/build-macos" "${COMMON_FLAGS[@]}" \
    -DGGML_METAL=ON -DGGML_METAL_EMBED_LIBRARY=ON \
    -DCMAKE_INSTALL_RPATH='@loader_path' -DCMAKE_BUILD_WITH_INSTALL_RPATH=ON
  cmake --build "$SRC/build-macos" --config Release -j "$JOBS"
  mkdir -p "$OUT/osx-arm64"
  cp "$SRC"/build-macos/bin/*.dylib "$OUT/osx-arm64/"
  echo "→ $OUT/osx-arm64"; ls -lh "$OUT/osx-arm64"
}

build_android() {
  echo "=== Android arm64-v8a (CPU) ==="
  local toolchain="$ANDROID_HOME/ndk/$NDK_VERSION/build/cmake/android.toolchain.cmake"
  [[ -f "$toolchain" ]] || { echo "FATAL: NDK toolchain not found at $toolchain" >&2; exit 1; }
  cmake -S "$SRC" -B "$SRC/build-android" "${COMMON_FLAGS[@]}" \
    -DCMAKE_TOOLCHAIN_FILE="$toolchain" \
    -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-26 \
    -DGGML_OPENMP=OFF -DGGML_METAL=OFF \
    -DCMAKE_SHARED_LINKER_FLAGS="-Wl,-z,max-page-size=16384"
  cmake --build "$SRC/build-android" --config Release -j "$JOBS"
  mkdir -p "$OUT/android-arm64"
  rm -f "$OUT/android-arm64/"*.so
  local strip="$ANDROID_HOME/ndk/$NDK_VERSION/toolchains/llvm/prebuilt/darwin-x86_64/bin/llvm-strip"
  # only what the app packages (libllama-common is a tools-side lib, not needed)
  for so in libllama.so libggml.so libggml-base.so libggml-cpu.so; do
    cp "$SRC/build-android/bin/$so" "$OUT/android-arm64/"
    "$strip" --strip-unneeded "$OUT/android-arm64/$so"
  done
  echo "→ $OUT/android-arm64"; ls -lh "$OUT/android-arm64"
}

build_ios() {
  echo "=== iOS xcframework (device + simulator; upstream build-xcframework.sh) ==="
  ( cd "$SRC" && ./build-xcframework.sh )
  mkdir -p "$OUT/ios"
  rm -rf "$OUT/ios/llama.xcframework"
  cp -R "$SRC/build-apple/llama.xcframework" "$OUT/ios/"
  echo "→ $OUT/ios/llama.xcframework"
}

fetch_sherpa_ios() {
  echo "=== sherpa-onnx iOS static xcframeworks (prebuilt v$SHERPA_VERSION) ==="
  local cache="${STORYTIME_NATIVE_SRC:-$HOME/.cache/storytime}/sherpa-onnx-v${SHERPA_VERSION}-ios.tar.bz2"
  mkdir -p "$(dirname "$cache")"
  if [[ ! -f "$cache" ]]; then
    curl -fL --retry 3 -o "$cache.part" "$SHERPA_IOS_URL"
    mv "$cache.part" "$cache"
  fi
  local got; got=$(shasum -a 256 "$cache" | awk '{print $1}')
  if [[ "$got" != "$SHERPA_IOS_SHA" ]]; then
    echo "FATAL: sherpa-onnx ios tarball sha256 $got != $SHERPA_IOS_SHA" >&2; exit 1
  fi
  local tmp; tmp=$(mktemp -d)
  tar -xjf "$cache" -C "$tmp"
  mkdir -p "$OUT/ios"
  rm -rf "$OUT/ios/sherpa-onnx.xcframework" "$OUT/ios/onnxruntime.xcframework"
  cp -R "$tmp/build-ios/sherpa-onnx.xcframework" "$OUT/ios/"
  cp -R "$tmp"/build-ios/ios-onnxruntime/*/onnxruntime.xcframework "$OUT/ios/"
  rm -rf "$tmp"
  echo "→ $OUT/ios/{sherpa-onnx,onnxruntime}.xcframework"
}

vendor_headers() {
  echo "=== Vendoring C headers (binding reference snapshot @ $LLAMA_TAG) ==="
  local dst="$REPO_ROOT/src/app/StoryTime.Core/Engines/Llama/vendor"
  mkdir -p "$dst"
  cp "$SRC/include/llama.h" "$dst/"
  cp "$SRC/ggml/include/ggml.h" "$SRC/ggml/include/ggml-backend.h" "$SRC/ggml/include/ggml-alloc.h" "$SRC/ggml/include/ggml-cpu.h" "$dst/"
  printf 'llama.cpp %s (%s) — header snapshot for LlamaNative.cs binding reference. Do not edit.\n' "$LLAMA_TAG" "$LLAMA_COMMIT" > "$dst/VENDORED.txt"
  echo "→ $dst"
}

target="${1:-all}"
[[ "$target" != "sherpa-ios" ]] && ensure_src   # llama source not needed for the prebuilt sherpa fetch
case "$target" in
  macos)      build_macos ;;
  android)    build_android ;;
  ios)        build_ios ;;
  sherpa-ios) fetch_sherpa_ios ;;
  headers)    vendor_headers ;;
  all)        vendor_headers; build_macos; build_android; build_ios; fetch_sherpa_ios ;;
  *) echo "Unknown target: $target (use macos|android|ios|sherpa-ios|headers|all)" >&2; exit 1 ;;
esac
echo "DONE: $target"
