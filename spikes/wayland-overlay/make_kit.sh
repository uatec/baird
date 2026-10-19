#!/bin/bash
# Builds the Pi kit: publishes OverlayTest for linux-arm64 (self-contained, no .NET needed
# on the Pi) and bundles it with the test script into baird-wayland-spike.tar.gz.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
KIT="kit/baird-wayland-spike"
rm -rf kit
mkdir -p "$KIT"
dotnet publish OverlayTest/OverlayTest.csproj -c Release -r linux-arm64 --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "kit/publish" >/dev/null
cp kit/publish/OverlayTest "$KIT/"
cp run_test.sh labwc-window-rules.xml README.md "$KIT/"
tar -C kit -czf kit/baird-wayland-spike.tar.gz baird-wayland-spike
echo "Kit: $(pwd)/kit/baird-wayland-spike.tar.gz"
