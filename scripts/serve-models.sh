#!/usr/bin/env bash
# serve-models.sh — serve $STORYTIME_MODEL_ROOT over LAN HTTP for the #if DEBUG
# device bootstrap (ModelProvider LAN fetch). Port 8787. Ctrl-C to stop.
set -euo pipefail
MODEL_ROOT="${STORYTIME_MODEL_ROOT:-$HOME/StoryTimeModels}"
PORT="${STORYTIME_MODEL_PORT:-8787}"
IP=$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || echo "0.0.0.0")
HOSTNAME_LOCAL="$(scutil --get LocalHostName 2>/dev/null || echo mac).local"
echo "Serving $MODEL_ROOT on http://$IP:$PORT  (Debug device bootstrap)"
echo "Android emulator uses http://10.0.2.2:$PORT; iOS devices/sim use http://$HOSTNAME_LOCAL:$PORT (baked at Debug build)"
for f in "Qwen3-1.7B-Q4_K_M.gguf" "kokoro-multi-lang-v1_0.bundle.tar"; do
  if [[ -f "$MODEL_ROOT/$f" ]]; then
    echo "  ready: $f ($(du -h "$MODEL_ROOT/$f" | cut -f1))"
  else
    echo "  MISSING: $f — run scripts/fetch-models.sh first" >&2
  fi
done
cd "$MODEL_ROOT"
exec python3 -m http.server "$PORT" --bind 0.0.0.0
