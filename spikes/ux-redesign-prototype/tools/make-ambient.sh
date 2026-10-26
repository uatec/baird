#!/usr/bin/env bash
# Pre-blurred tiny copies of each backdrop for the ambient light behind the UI (cheap to scale up, even on the Pi).
cd "$(dirname "$0")/../assets/art" && mkdir -p ambient
for f in *-backdrop.jpg; do k=${f%-backdrop.jpg}; magick "$f" -resize 96x54 -blur 0x6 -modulate 100,140 -quality 80 "ambient/$k.jpg"; done
