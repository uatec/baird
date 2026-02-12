#!/bin/bash
set -e

# Define output directory
OUTPUT_DIR="./publish"
mkdir -p "$OUTPUT_DIR"

echo "Cleaning previous builds..."
dotnet clean Baird/Baird.csproj
rm -rf "$OUTPUT_DIR"

echo "Building for Raspberry Pi (linux-arm64)..."
dotnet publish Baird/Baird.csproj \
    -c Debug \
    -r linux-arm64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -p:PublishTrimmed=true \
    -p:Version="${BUILD_VERSION:-1.0.0}" \
    -o "$OUTPUT_DIR/linux-arm64"
    
cp 99-baird.rules "$OUTPUT_DIR/linux-arm64/"
cp install_deps.sh "$OUTPUT_DIR/linux-arm64/"
cp baird.service "$OUTPUT_DIR/linux-arm64/"
cp install_service.sh "$OUTPUT_DIR/linux-arm64/"
cp update_baird.sh "$OUTPUT_DIR/linux-arm64/"
cp baird-updater.service "$OUTPUT_DIR/linux-arm64/"
cp baird-updater.timer "$OUTPUT_DIR/linux-arm64/"

