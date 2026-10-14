#!/usr/bin/env bash
# Build StoryTime for a wirelessly paired iPhone, install and launch it, and serve the API +
# web app from this Mac on the local network so the phone can reach them.
#
# Usage (macOS with Xcode 27, XcodeGen, .NET 10, Node 22):
#   STORYTIME_TEAM_ID=ABCDE12345 make ios-device
#
# Optional:
#   STORYTIME_DEVICE          device name / model to match (default: "iPhone Air")
#   STORYTIME_BUNDLE_ID       bundle identifier (default: com.frostaura.storytime)
#   STORYTIME_LAN_IP          this Mac's LAN IP (default: autodetected from en0/en1)
#   STORYTIME_API_PORT        API port (default: 8080)
#   STORYTIME_WEB_PORT        web app port (default: 5173)
#   STORYTIME_APPROVAL_REQUIRED  parent approval default (default: false; passkeys need HTTPS + a real domain)
#   STORYTIME_SERVE           set to 0 to skip serving the API/web app (e.g. pointing at a deployed stack)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEAM_ID="${STORYTIME_TEAM_ID:-}"
DEVICE_QUERY="${STORYTIME_DEVICE:-iPhone Air}"
BUNDLE_ID="${STORYTIME_BUNDLE_ID:-com.frostaura.storytime}"
API_PORT="${STORYTIME_API_PORT:-8080}"
WEB_PORT="${STORYTIME_WEB_PORT:-5173}"
APPROVAL_REQUIRED="${STORYTIME_APPROVAL_REQUIRED:-false}"
SERVE="${STORYTIME_SERVE:-1}"
BUILD_DIR="$ROOT/build/ios-device"
LOG_DIR="$BUILD_DIR/logs"

fail() { echo "✖ $*" >&2; exit 1; }
step() { echo "▸ $*"; }

[[ "$(uname)" == "Darwin" ]] || fail "Run this on a Mac with Xcode installed."
[[ -n "$TEAM_ID" ]] || fail "Set STORYTIME_TEAM_ID to your Apple Developer Team ID (Xcode › Settings › Accounts › Team)."
for tool in xcodebuild xcrun xcodegen python3 curl; do
  command -v "$tool" >/dev/null || fail "Missing '$tool'. Install Xcode 27 and 'brew install xcodegen'."
done
if [[ "$SERVE" == "1" ]]; then
  for tool in dotnet npm; do command -v "$tool" >/dev/null || fail "Missing '$tool' (needed to serve the API/web app)."; done
fi

LAN_IP="${STORYTIME_LAN_IP:-$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1 2>/dev/null || true)}"
[[ -n "$LAN_IP" ]] || fail "Could not detect this Mac's LAN IP; set STORYTIME_LAN_IP."
WEB_URL="${STORYTIME_WEB_APP_URL:-http://$LAN_IP:$WEB_PORT}"
API_URL="${STORYTIME_API_BASE_URL:-http://$LAN_IP:$API_PORT}"
mkdir -p "$LOG_DIR"

step "Looking for '$DEVICE_QUERY' (paired for wireless debugging)…"
DEVICES_JSON="$BUILD_DIR/devices.json"
xcrun devicectl list devices --json-output "$DEVICES_JSON" >/dev/null
DEVICE_UDID="$(python3 - "$DEVICES_JSON" "$DEVICE_QUERY" <<'PY'
import json, sys
devices = json.load(open(sys.argv[1]))["result"]["devices"]
query = sys.argv[2].lower()

def label(device):
    props, hw = device.get("deviceProperties", {}), device.get("hardwareProperties", {})
    return f'{props.get("name", "")} {hw.get("marketingName", "")}'.lower()

def rank(device):
    conn = device.get("connectionProperties", {})
    return (conn.get("tunnelState") == "connected", conn.get("pairingState") == "paired")

matches = [d for d in devices if query in label(d) and d.get("hardwareProperties", {}).get("platform") == "iOS"]
if matches:
    print(sorted(matches, key=rank, reverse=True)[0]["hardwareProperties"]["udid"])
PY
)"
[[ -n "$DEVICE_UDID" ]] || fail "No paired device matching '$DEVICE_QUERY'. Pair it once in Xcode › Window › Devices and Simulators (tick 'Connect via network'), keep it unlocked on the same Wi-Fi."
step "Using device $DEVICE_UDID"

PIDS=()
cleanup() { for pid in "${PIDS[@]:-}"; do [[ -n "$pid" ]] && kill "$pid" 2>/dev/null || true; done; }
trap cleanup EXIT INT TERM

wait_for() {
  local url="$1" name="$2"
  for _ in $(seq 1 120); do
    curl -fsS -o /dev/null "$url" 2>/dev/null && return 0
    sleep 1
  done
  fail "$name did not start; see $LOG_DIR."
}

if [[ "$SERVE" == "1" ]]; then
  step "Serving API on $API_URL (parent approval default: $APPROVAL_REQUIRED)…"
  ASPNETCORE_URLS="http://0.0.0.0:$API_PORT" \
  ASPNETCORE_ENVIRONMENT=Development \
  StoryTime__DefaultApprovalRequired="$APPROVAL_REQUIRED" \
  StoryTime__Cors__AllowedOrigins__0="$WEB_URL" \
    dotnet run --no-launch-profile --project "$ROOT/src/backend/StoryTime.Api" >"$LOG_DIR/api.log" 2>&1 &
  PIDS+=("$!")

  step "Serving web app on $WEB_URL…"
  [[ -d "$ROOT/src/frontend/node_modules" ]] || (cd "$ROOT/src/frontend" && npm ci --no-audit --no-fund)
  (cd "$ROOT/src/frontend" && VITE_API_BASE_URL="$API_URL" npm run dev -- --host 0.0.0.0 --port "$WEB_PORT") \
    >"$LOG_DIR/web.log" 2>&1 &
  PIDS+=("$!")

  wait_for "http://127.0.0.1:$API_PORT/api/home/status" "API"
  wait_for "http://127.0.0.1:$WEB_PORT/" "Web app"
fi

step "Generating Xcode project…"
(cd "$ROOT/src/ios" && xcodegen generate --quiet)

step "Building for device (automatic signing, team $TEAM_ID)…"
xcodebuild \
  -project "$ROOT/src/ios/StoryTime.xcodeproj" \
  -scheme StoryTime \
  -configuration Debug \
  -destination "id=$DEVICE_UDID" \
  -derivedDataPath "$BUILD_DIR/DerivedData" \
  -allowProvisioningUpdates \
  -allowProvisioningDeviceRegistration \
  DEVELOPMENT_TEAM="$TEAM_ID" \
  PRODUCT_BUNDLE_IDENTIFIER="$BUNDLE_ID" \
  STORYTIME_WEB_APP_URL="$WEB_URL" \
  STORYTIME_API_BASE_URL="$API_URL" \
  build >"$LOG_DIR/xcodebuild.log" 2>&1 || { tail -40 "$LOG_DIR/xcodebuild.log"; fail "Build failed; full log: $LOG_DIR/xcodebuild.log"; }

APP_PATH="$BUILD_DIR/DerivedData/Build/Products/Debug-iphoneos/StoryTime.app"
step "Installing over Wi-Fi…"
xcrun devicectl device install app --device "$DEVICE_UDID" "$APP_PATH"

step "Launching…"
xcrun devicectl device process launch --device "$DEVICE_UDID" --terminate-existing "$BUNDLE_ID" >/dev/null

echo "✔ StoryTime is running on your device."
echo "  First launch: allow Local Network + Microphone, and trust the developer under"
echo "  Settings › General › VPN & Device Management if iOS asks."
if [[ "$SERVE" == "1" ]]; then
  echo "  Keep this terminal open while testing (API + web app logs: $LOG_DIR). Ctrl-C to stop."
  wait
fi
