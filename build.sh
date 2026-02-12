#!/bin/bash
set -e

# Build script for Baird with AOT (Ahead-of-Time) compilation
# 
# AOT compilation provides improved startup performance and reduced memory usage.
# For cross-compilation (building for ARM64 on x86_64), you need to install:
#   - clang
#   - lld
#   - ARM64 cross-compilation toolchain
#
# On Ubuntu/Debian: sudo apt-get install clang lld gcc-aarch64-linux-gnu
#
# Set TARGET_PLATFORM environment variable to override the target platform.
# Default: linux-arm64

# Define output directory
OUTPUT_DIR="./publish"
mkdir -p "$OUTPUT_DIR"

# Determine target platform
TARGET_PLATFORM="${TARGET_PLATFORM:-linux-arm64}"

echo "Cleaning previous builds..."
dotnet clean Baird/Baird.csproj
rm -rf "$OUTPUT_DIR"

echo "Building for ${TARGET_PLATFORM} with AOT compilation..."
dotnet publish Baird/Baird.csproj \
    -c Release \
    -r "$TARGET_PLATFORM" \
    --self-contained true \
    -p:PublishAot=true \
    -p:PublishTrimmed=true \
    -p:Version="${BUILD_VERSION:-1.0.0}" \
    -o "$OUTPUT_DIR/$TARGET_PLATFORM"
    
cp 99-baird.rules "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp install_deps.sh "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp baird.service "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp install_service.sh "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp update_baird.sh "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp baird-updater.service "$OUTPUT_DIR/$TARGET_PLATFORM/"
cp baird-updater.timer "$OUTPUT_DIR/$TARGET_PLATFORM/"

echo ""
echo "Verifying AOT artifact creation..."
if [ -f "$OUTPUT_DIR/$TARGET_PLATFORM/Baird" ]; then
    echo "✓ AOT binary 'Baird' successfully created"
    echo "  Location: $OUTPUT_DIR/$TARGET_PLATFORM/Baird"
    # Show size of the binary
    SIZE=$(ls -lh "$OUTPUT_DIR/$TARGET_PLATFORM/Baird" | awk '{print $5}')
    echo "  Size: $SIZE"
    # Verify it's an ELF binary for the target platform
    FILE_TYPE=$(file "$OUTPUT_DIR/$TARGET_PLATFORM/Baird" 2>/dev/null || echo "file command not available")
    echo "  Type: $FILE_TYPE"
else
    echo "✗ ERROR: AOT binary not found at $OUTPUT_DIR/$TARGET_PLATFORM/Baird"
    exit 1
fi

echo ""
echo "Build completed successfully!"
