#!/usr/bin/env bash
# Builds linux-x64 librive.so in a Debian 12 container so it only needs glibc >= 2.29 (runs on CI, Ubuntu, Manjaro, Pi-class distros).
# Usage: RIVE_SHARP=/path/to/rive-sharp-checkout ./build-x64.sh   (checkout needs `submodule update --init`)
# Output: Baird/runtimes/linux-x64/native/librive.so
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
OUT_DIR=$HERE/../../../Baird/runtimes/linux-x64/native
S=${RIVE_SHARP:?set RIVE_SHARP to a rive-sharp checkout}
mkdir -p "$OUT_DIR"
docker build -q -t rive-x64-build -f "$HERE/Dockerfile.x64" "$HERE"
docker run --rm -v "$S":/src:ro -v "$(cd "$OUT_DIR" && pwd)":/out rive-x64-build bash -c '
set -e; R=/src/submodules/rive-cpp; cd /tmp
find $R/src -name "*.cpp" > files.txt; echo /src/native/RiveSharpInterop.cpp >> files.txt
clang++ -std=c++17 -O2 -fPIC -shared -DWASM -D_RIVE_INTERNAL_ -DNDEBUG -I$R/include -w $(cat files.txt) -o /out/librive.so
strip --strip-unneeded /out/librive.so'
file "$OUT_DIR/librive.so"
