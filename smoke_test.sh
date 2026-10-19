#!/bin/bash
# Startup smoke test for a published Baird binary.
#
# Launches the binary under a virtual X server (xvfb-run) with an empty HOME
# (so no ~/.baird/config.ini) and asserts that it is still alive after
# SMOKE_SECONDS. A crash at startup -- e.g. trimming removing something that
# is only reached at runtime, a missing native library, a XAML load failure --
# makes the process exit early and fails the test.
#
# Usage: ./smoke_test.sh [path/to/Baird]
#   default binary: publish/linux-arm64/Baird (output of ./build.sh)
# Env:
#   SMOKE_SECONDS  how long the app must stay up (default 25)
#
# Requires: xvfb-run (package xvfb), libmpv (Ubuntu 24.04: libmpv2).
# The same script is run by CI on an arm64 runner and by humans locally.
set -euo pipefail

BINARY="${1:-publish/linux-arm64/Baird}"
SMOKE_SECONDS="${SMOKE_SECONDS:-25}"

if [[ ! -f "$BINARY" ]]; then
    echo "FAIL: binary not found: $BINARY" >&2
    exit 1
fi
chmod +x "$BINARY"
BINARY="$(readlink -f "$BINARY")"

if ! command -v xvfb-run >/dev/null 2>&1; then
    echo "FAIL: xvfb-run not found (install the 'xvfb' package)" >&2
    exit 1
fi

WORK_DIR="$(mktemp -d)"
LOG_FILE="$WORK_DIR/baird.log"
APP_PID=""

cleanup() {
    if [[ -n "$APP_PID" ]] && kill -0 "$APP_PID" 2>/dev/null; then
        kill -TERM -- "-$APP_PID" 2>/dev/null || kill -TERM "$APP_PID" 2>/dev/null || true
        sleep 2
        kill -KILL -- "-$APP_PID" 2>/dev/null || true
    fi
    rm -rf "$WORK_DIR"
}
trap cleanup EXIT

echo "Smoke test: starting $BINARY under xvfb for ${SMOKE_SECONDS}s (isolated HOME, no config)"

# Isolated HOME: the app must start with no config (services fail soft).
# Run from WORK_DIR so ./config.ini in the cwd isn't picked up either.
# setsid gives xvfb-run + Xvfb + Baird their own process group so cleanup
# can kill the lot. BAIRD_FULLSCREEN is left unset (windowed desktop mode).
(
    cd "$WORK_DIR"
    export HOME="$WORK_DIR/home"
    mkdir -p "$HOME"
    unset BAIRD_FULLSCREEN XDG_CONFIG_HOME XDG_CACHE_HOME
    exec setsid xvfb-run --auto-servernum --server-args="-screen 0 1920x1080x24" \
        "$BINARY"
) >"$LOG_FILE" 2>&1 &
APP_PID=$!

alive=true
for ((i = 0; i < SMOKE_SECONDS; i++)); do
    if ! kill -0 "$APP_PID" 2>/dev/null; then
        alive=false
        break
    fi
    sleep 1
done

exit_code=""
if [[ "$alive" == false ]]; then
    set +e
    wait "$APP_PID"
    exit_code=$?
    set -e
    APP_PID=""
fi

echo "----- Baird output (last 200 lines) -----"
tail -n 200 "$LOG_FILE" || true
echo "-----------------------------------------"

if [[ "$alive" == false ]]; then
    echo "FAIL: Baird exited after ~${i}s with code ${exit_code} (expected to still be running after ${SMOKE_SECONDS}s)" >&2
    exit 1
fi

# Unhandled .NET exceptions normally kill the process, but flag them anyway
# in case one was swallowed on a background thread.
if grep -q "Unhandled exception" "$LOG_FILE"; then
    echo "FAIL: 'Unhandled exception' found in output" >&2
    exit 1
fi

echo "PASS: Baird still running after ${SMOKE_SECONDS}s"
