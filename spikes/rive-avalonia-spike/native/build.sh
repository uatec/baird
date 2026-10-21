#!/usr/bin/env bash
# Builds librive.so (rive-cpp runtime + RiveSharpInterop.cpp) with plain clang++, no premake.
# Usage: RIVE_SHARP=/path/to/rive-sharp-checkout ./build.sh <outdir> [extra clang flags]
#   rive-sharp checkout must have its submodule initialised:
#   git clone https://github.com/rive-app/rive-sharp && git -C rive-sharp submodule update --init
# -DWASM selects the non-__declspec export macro in RiveSharpInterop.cpp (visibility("default")).
set -e
S=${RIVE_SHARP:?set RIVE_SHARP}
R=$S/submodules/rive-cpp
OUT=$1; shift
mkdir -p "$OUT"; cd "$OUT"
find "$R/src" -name '*.cpp' > files.txt
echo "$S/native/RiveSharpInterop.cpp" >> files.txt
clang++ -std=c++17 -O2 -fPIC -shared -DWASM -D_RIVE_INTERNAL_ -DNDEBUG -I"$R/include" -w "$@" $(cat files.txt) -o librive.so
