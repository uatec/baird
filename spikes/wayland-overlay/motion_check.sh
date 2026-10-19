#!/bin/bash
# Objective "is the picture actually moving?" check — mpv's own counters can look healthy while
# nothing reaches the screen (e.g. Vulkan swapchain failures leave one frozen frame on the TV).
#
# Captures N frames ~200 ms apart and counts how many distinct images there are in a strip of
# the video area that the overlay never covers (left 500x900 px: clear of the bottom HUD bar and
# the centred menu panel). Live video ≈ N/N; frozen ≈ 1/N; choppy = in between.
#
# Usage: motion_check.sh wayland|x11 [frames=10]   → prints "distinct/frames" (e.g. "10/10")
# Needs: ffmpeg; grim for wayland (DISPLAY for x11).
set -euo pipefail
MODE="$1"
N="${2:-10}"
CROP="crop=500:900:0:0"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [[ "$MODE" == wayland ]]; then
    for ((i = 1; i <= N; i++)); do
        grim -t ppm "$(printf '%s/m%02d.ppm' "$TMP" "$i")"
        sleep 0.2
    done
    hashes="$(ffmpeg -loglevel error -i "$TMP/m%02d.ppm" -vf "$CROP" -f framemd5 - 2>/dev/null)"
else
    hashes="$(ffmpeg -loglevel error -f x11grab -video_size 1920x1080 -framerate 5 -i "${DISPLAY:-:0}" \
        -frames:v "$N" -vf "$CROP" -f framemd5 - 2>/dev/null)"
fi
distinct="$(grep -v '^#' <<<"$hashes" | awk -F, '{gsub(/ /, "", $NF); print $NF}' | sort -u | grep -c . || true)"
echo "$distinct/$N"
