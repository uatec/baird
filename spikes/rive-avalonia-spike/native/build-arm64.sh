#!/usr/bin/env bash
# Cross-compiles librive.so for linux-arm64 (Raspberry Pi) in a Debian 12 container, so the result only needs
# glibc >= 2.29 / libstdc++ 3.4.29 and loads on Pi OS Bookworm and newer.
# Usage: RIVE_SHARP=/path/to/rive-sharp-checkout ./build-arm64.sh   (checkout needs `submodule update --init`)
# Output: Baird/runtimes/linux-arm64/native/librive.so
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT_DIR=$HERE/../../../Baird/runtimes/linux-arm64/native
S=${RIVE_SHARP:?set RIVE_SHARP to a rive-sharp checkout}
mkdir -p "$OUT_DIR"
docker build -q -t rive-arm64-build -f "$HERE/Dockerfile.arm64" "$HERE"
docker run --rm -v "$S":/src:ro -v "$(cd "$OUT_DIR" && pwd)":/out rive-arm64-build bash -c '
set -e; R=/src/submodules/rive-cpp; cd /tmp
find $R/src -name "*.cpp" > files.txt; echo /src/native/RiveSharpInterop.cpp >> files.txt
clang++ --target=aarch64-linux-gnu --sysroot=/ -I/usr/aarch64-linux-gnu/include -I/usr/aarch64-linux-gnu/include/c++/12 \
  -I/usr/aarch64-linux-gnu/include/c++/12/aarch64-linux-gnu -fuse-ld=lld -L/usr/lib/gcc-cross/aarch64-linux-gnu/12 \
  -L/usr/aarch64-linux-gnu/lib -std=c++17 -O2 -fPIC -shared -DWASM -D_RIVE_INTERNAL_ -DNDEBUG -I$R/include -w \
  $(cat files.txt) -o /out/librive.so -lstdc++
aarch64-linux-gnu-strip --strip-unneeded /out/librive.so'
file "$OUT_DIR/librive.so"
