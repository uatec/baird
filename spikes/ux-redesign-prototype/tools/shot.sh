#!/usr/bin/env bash
# Usage: tools/shot.sh <out.png> [hash-route] [query]   — headless 1920×1080 screenshot, console errors to stderr.
OUT=$1; ROUTE=${2:-}; Q=${3:-}
google-chrome-stable --headless=new --disable-gpu --hide-scrollbars --window-size=1920,1080 --virtual-time-budget=${BUDGET:-12000} \
  --enable-logging=stderr --v=0 --user-data-dir=/tmp/baird-shot-profile \
  --screenshot="$OUT" "http://100.109.40.12:8077/${Q:+?$Q}${ROUTE:+#$ROUTE}" 2>&1 | grep -E "CONSOLE|Uncaught|ERROR:.*(js|module)" | grep -v -E "gpu|dbus|Fontconfig" | head -10
