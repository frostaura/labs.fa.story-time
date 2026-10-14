#!/usr/bin/env bash
# StoryTime voice lab: render the fixture story through the REAL SoothingNarrator chain with
# macOS AVSpeechSynthesizer voices, and measure it. Re-runnable; outputs go to $OUT.
#   src/tools/StoryTime.NarrationLab/run-lab.sh [voice ...]
# Default voices: Samantha Moira Daniel (+ Serena when a Serena voice is installed).
# PROFILE=E|F|G|voice-lab picks a named MasteringProfile (default: the shipped one) and
# prefixes every output with it, e.g. PROFILE=E → $OUT/E-samantha.m4a.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../../.." && pwd)"
OUT="${OUT:-/private/tmp/storytime-narration}"
STORY="${STORY:-$HERE/story.txt}"
BUILD="$OUT/build"
PROFILE="${PROFILE:-}"
PROFILE_ARGS=()
PREFIX=""
if [ -n "$PROFILE" ]; then PROFILE_ARGS=(--profile "$PROFILE"); PREFIX="$PROFILE-"; fi
mkdir -p "$OUT" "$BUILD"
source "$REPO/src/build-env.sh"

dotnet build "$HERE/StoryTime.NarrationLab.csproj" -c Release -v q -nologo -o "$BUILD/lab" >/dev/null
LAB="dotnet $BUILD/lab/StoryTime.NarrationLab.dll"
swiftc -O "$HERE/render-utterances.swift" -o "$BUILD/render-utterances" 2>/dev/null
SWIFT="$BUILD/render-utterances"

VOICES=("$@")
if [ ${#VOICES[@]} -eq 0 ]; then
  VOICES=(Samantha Moira Daniel)
  if "$SWIFT" voices | cut -f2 | grep -q '^Serena'; then VOICES+=(Serena); fi
fi

$LAB plan "$STORY" "$OUT/plan.json"

# Baseline: plain AVSpeechSynthesizer, one utterance, default rate, no processing.
"$SWIFT" baseline "$STORY" Samantha "$OUT/pcm-baseline" >/dev/null
RATE=$(python3 -c "import json;print(json.load(open('$OUT/pcm-baseline/0000.json'))['rate'])")
ffmpeg -loglevel error -y -f f32le -ar "$RATE" -ac 1 -i "$OUT/pcm-baseline/0000.f32" -ar 48000 -c:a pcm_s16le "$OUT/baseline-samantha.wav"
ffmpeg -loglevel error -y -i "$OUT/baseline-samantha.wav" -c:a aac -b:a 160k "$OUT/baseline-samantha.m4a"
$LAB measure "$OUT/baseline-samantha.wav" > "$OUT/baseline-samantha.report.json"

for V in "${VOICES[@]}"; do
  voice=$(echo "$V" | tr '[:upper:]' '[:lower:]')
  slug="$PREFIX$voice"
  "$SWIFT" plan "$OUT/plan.json" "$V" "$OUT/pcm-$voice" >/dev/null
  $LAB mix "$STORY" "$OUT/pcm-$voice" "$OUT/$slug.wav" ${PROFILE_ARGS[@]+"${PROFILE_ARGS[@]}"} > /dev/null
  $LAB shift-check "$OUT/pcm-$voice" ${PROFILE_ARGS[@]+"${PROFILE_ARGS[@]}"} > "$OUT/$slug.shift-check.txt"
  ffmpeg -loglevel error -y -i "$OUT/$slug.wav" -c:a aac -b:a 160k "$OUT/$slug.m4a"
  # Independent loudness cross-check (ffmpeg ebur128 measures mono as one channel, like ours).
  ffmpeg -nostats -hide_banner -i "$OUT/$slug.wav" -af ebur128=peak=true -f null - 2>&1 \
    | grep -E "^\s+(I:|Peak:)" | tr -s ' ' > "$OUT/$slug.ffmpeg-ebur128.txt" || true
done

python3 - "$OUT" "$PREFIX" "${VOICES[@]}" <<'PY'
import json, sys, os
out, prefix, voices = sys.argv[1], sys.argv[2], sys.argv[3:]
rows = []
b = json.load(open(os.path.join(out, "baseline-samantha.report.json")))
print(f"{'sample':<20}{'sec':>7}{'wpm':>7}{'LUFS':>8}{'dBTP':>7}{'padLU':>7}{'pause|err| med/max ms':>24}{'≤80 −45/−50':>14}{'x RT':>7}")
print(f"{'baseline-samantha':<20}{b['seconds']:>7.1f}{'':>7}{b['integratedLufs']:>8.2f}{b['truePeakDbtp']:>7.2f}")
for v in voices:
    r = json.load(open(os.path.join(out, prefix + v.lower() + ".report.json")))
    print(f"{prefix + v.lower():<20}{r['seconds']:>7.1f}{r['wpmStory']:>7.1f}{r['integratedLufs']:>8.2f}{r['truePeakDbtp']:>7.2f}"
          f"{(r['padLuBelowVoice'] or 0):>7.1f}{str(r['pauseErrorMedianAbsMs'])+' / '+str(r['pauseErrorMaxAbsMs']):>24}{r['pausesWithin80ms']+' '+r['pausesWithin80msStrict']:>14}{r['realtimeFactor']:>7.1f}")
PY
echo "outputs: $OUT"
