#!/usr/bin/env bash
# fetch-models.sh — download StoryTime model binaries into $STORYTIME_MODEL_ROOT
# (default ~/StoryTimeModels — ALWAYS outside the iCloud-synced repo tree).
#
# Idempotent: verified files are stamped (<file>.sha256.ok) and skipped on re-run.
# Usage:
#   scripts/fetch-models.sh                  # LLM (Qwen3-1.7B Q4_K_M) + TTS (kokoro-multi-lang-v1_0, fp32 quality tier)
#   scripts/fetch-models.sh --with-fallback  # also fetch Qwen3-0.6B Q4_K_M low-RAM tier
#   scripts/fetch-models.sh --tts-int8       # ALSO fetch the int8 low-storage TTS tier and
#                                            # pack the device bundle tar FROM IT instead
#
# TTS tiers (founder decision 2026-07-30: audible quality outranks size):
#   quality tier (default): kokoro-multi-lang-v1_0 fp32 — model.onnx 326 MB, 53 voices
#     (af_heart etc.), ~377 MB on device after zh-prune. Personas: Ember=af_heart(3),
#     Willow=af_bella(2), Sage=am_michael(16).
#   low-storage tier (flag): kokoro-int8-multi-lang-v1_0 — model.int8.onnx 114 MB,
#     same voices, ~165 MB on device. Same directory name on device; the engine prefers
#     model.onnx and falls back to model.int8.onnx.
#
# Pinned artifacts (verified 2026-07-30):
#   Qwen3-1.7B-Q4_K_M.gguf  1,107,409,472 B
#     https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf
#     sha256 b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897
#   Qwen3-0.6B-Q4_K_M.gguf    396,705,472 B (optional fallback tier)
#     https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q4_K_M.gguf
#     sha256 ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a
#   kokoro-multi-lang-v1_0.tar.bz2  349,418,188 B → kokoro-multi-lang-v1_0/ (fp32)
#     https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2
#     sha256 c133d26353d776da730870dac7da07dbfc9a5e3bc80cc5e8e83ab6e823be7046
#   kokoro-int8-multi-lang-v1_0.tar.bz2  131,839,838 B → kokoro-int8-multi-lang-v1_0/
#     https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_0.tar.bz2
#     sha256 75654a84864be26f345f020f4070c2c019e96dd1b7f9bf6e2ffd59efac6aa5a3
#   (legacy kokoro-en-v0_19 int8 is no longer fetched; existing extractions keep working)
set -euo pipefail

MODEL_ROOT="${STORYTIME_MODEL_ROOT:-$HOME/StoryTimeModels}"
mkdir -p "$MODEL_ROOT"

QWEN17_URL="https://huggingface.co/unsloth/Qwen3-1.7B-GGUF/resolve/main/Qwen3-1.7B-Q4_K_M.gguf"
QWEN17_SHA="b139949c5bd74937ad8ed8c8cf3d9ffb1e99c866c823204dc42c0d91fa181897"
QWEN06_URL="https://huggingface.co/unsloth/Qwen3-0.6B-GGUF/resolve/main/Qwen3-0.6B-Q4_K_M.gguf"
QWEN06_SHA="ac2d97712095a558e31573f62f466a3f9d93990898b0ec79d7c974c1780d524a"
KOKORO_FP32_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2"
KOKORO_FP32_SHA="c133d26353d776da730870dac7da07dbfc9a5e3bc80cc5e8e83ab6e823be7046"
KOKORO_INT8_URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-int8-multi-lang-v1_0.tar.bz2"
KOKORO_INT8_SHA="75654a84864be26f345f020f4070c2c019e96dd1b7f9bf6e2ffd59efac6aa5a3"

TTS_DIR_NAME="kokoro-multi-lang-v1_0"   # canonical on-device directory name for BOTH tiers

WITH_FALLBACK=0
TTS_INT8=0
for arg in "$@"; do
  case "$arg" in
    --with-fallback) WITH_FALLBACK=1 ;;
    --tts-int8) TTS_INT8=1 ;;
    *) echo "Unknown option: $arg (use --with-fallback and/or --tts-int8)" >&2; exit 1 ;;
  esac
done

fetch() { # url dest sha
  local url="$1" dest="$2" sha="$3"
  if [[ -f "$dest.sha256.ok" && -f "$dest" ]]; then
    echo "OK (cached): $dest"; return 0
  fi
  echo "Fetching $(basename "$dest") ..."
  curl -fL --retry 3 --continue-at - -o "$dest.part" "$url"
  local got
  got=$(shasum -a 256 "$dest.part" | awk '{print $1}')
  if [[ "$got" != "$sha" ]]; then
    echo "SHA256 MISMATCH for $dest: got $got want $sha" >&2
    rm -f "$dest.part"; exit 1
  fi
  mv "$dest.part" "$dest"
  echo "$sha" > "$dest.sha256.ok"
  echo "OK: $dest"
}

prune_zh() { # dir — drop Chinese-only assets the app never uses (saves ~17 MB device-side)
  local dir="$1"
  rm -rf "$dir/dict"
  rm -f "$dir/lexicon-zh.txt" "$dir/date-zh.fst" "$dir/number-zh.fst" "$dir/phone-zh.fst"
}

extract_tts() { # tarball extracted_dir
  local tarball="$1" dir="$2"
  if [[ ! -f "$MODEL_ROOT/$dir/.extracted.ok" ]]; then
    echo "Extracting $(basename "$tarball") ..."
    tar -xjf "$tarball" -C "$MODEL_ROOT"
    prune_zh "$MODEL_ROOT/$dir"
    touch "$MODEL_ROOT/$dir/.extracted.ok"
    echo "OK: $MODEL_ROOT/$dir"
  else
    echo "OK (cached): $MODEL_ROOT/$dir"
  fi
}

# --- LLM ---
fetch "$QWEN17_URL" "$MODEL_ROOT/Qwen3-1.7B-Q4_K_M.gguf" "$QWEN17_SHA"
if [[ $WITH_FALLBACK -eq 1 ]]; then
  fetch "$QWEN06_URL" "$MODEL_ROOT/Qwen3-0.6B-Q4_K_M.gguf" "$QWEN06_SHA"
fi

# --- TTS quality tier (fp32) — always fetched; it is the default the tools resolve ---
fetch "$KOKORO_FP32_URL" "$MODEL_ROOT/kokoro-multi-lang-v1_0.tar.bz2" "$KOKORO_FP32_SHA"
extract_tts "$MODEL_ROOT/kokoro-multi-lang-v1_0.tar.bz2" "kokoro-multi-lang-v1_0"

# --- TTS low-storage tier (int8) — on demand ---
if [[ $TTS_INT8 -eq 1 ]]; then
  fetch "$KOKORO_INT8_URL" "$MODEL_ROOT/kokoro-int8-multi-lang-v1_0.tar.bz2" "$KOKORO_INT8_SHA"
  extract_tts "$MODEL_ROOT/kokoro-int8-multi-lang-v1_0.tar.bz2" "kokoro-int8-multi-lang-v1_0"
fi

# --- device-bootstrap tar (single-file download for the #if DEBUG LAN bootstrap;
#     espeak-ng-data alone is 120+ files, unusable over plain http.server).
#     Top-level dir inside the tar is ALWAYS $TTS_DIR_NAME regardless of tier. ---
BUNDLE_TAR="$MODEL_ROOT/$TTS_DIR_NAME.bundle.tar"
TAR_OPTS=(--exclude '.extracted.ok' --exclude '._*' --exclude '.DS_Store')
if [[ $TTS_INT8 -eq 1 ]]; then
  echo "Packing $TTS_DIR_NAME.bundle.tar (int8 low-storage tier) ..."
  COPYFILE_DISABLE=1 tar -cf "$BUNDLE_TAR" "${TAR_OPTS[@]}" -s "/^kokoro-int8-multi-lang-v1_0/$TTS_DIR_NAME/" -C "$MODEL_ROOT" kokoro-int8-multi-lang-v1_0
else
  if [[ ! -f "$BUNDLE_TAR" || "$MODEL_ROOT/$TTS_DIR_NAME/.extracted.ok" -nt "$BUNDLE_TAR" ]]; then
    echo "Packing $TTS_DIR_NAME.bundle.tar (fp32 quality tier) ..."
    COPYFILE_DISABLE=1 tar -cf "$BUNDLE_TAR" "${TAR_OPTS[@]}" -C "$MODEL_ROOT" "$TTS_DIR_NAME"
  fi
fi

echo
echo "Model root: $MODEL_ROOT"
ls -lh "$MODEL_ROOT"
