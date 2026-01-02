#!/bin/bash
set -e

# Define output directory
OUTPUT_DIR="./publish"
mkdir -p "$OUTPUT_DIR"

echo "Cleaning previous builds..."
dotnet clean
rm -rf "$OUTPUT_DIR"

echo "Building for Raspberry Pi (linux-arm64)..."
dotnet publish Baird/Baird.csproj \
    -c Release \
    -r linux-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o "$OUTPUT_DIR/linux-arm64"

cp 99-baird.rules "$OUTPUT_DIR/"
cp install_deps.sh "$OUTPUT_DIR/"

echo "Building for Virtual Machine (linux-x64)..."
dotnet publish Baird/Baird.csproj \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -o "$OUTPUT_DIR/linux-x64"

echo "Build complete. Artifacts are in $OUTPUT_DIR"
