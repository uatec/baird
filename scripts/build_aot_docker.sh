#!/bin/bash
set -e

# Output build artifacts directory
OUTPUT_DIR="./publish/linux-arm64"
mkdir -p "$OUTPUT_DIR"

# Docker image to use for building (official .NET SDK image)
DOCKER_IMAGE="mcr.microsoft.com/dotnet/sdk:9.0"

echo "Detected macOS environment. Using Docker to build for Linux ARM64..."

if ! command -v docker &> /dev/null; then
    echo "Error: Docker is required to build Linux AOT binaries on macOS."
    echo "Please install Docker Desktop and ensure it is running."
    exit 1
fi

echo "Building inside Docker container..."
docker run --rm \
    -v "$(pwd):/source" \
    -w /source \
    -e BUILD_VERSION="${BUILD_VERSION:-0.0.0}" \
    "$DOCKER_IMAGE" \
    /bin/bash -c "
        apt-get update && apt-get install -y clang zlib1g-dev python3 && \
        dotnet publish Baird/Baird.csproj \
        -c Release \
        -r linux-arm64 \
        --self-contained true \
        -p:PublishAot=true \
        -p:PublishTrimmed=true \
        -p:Version='${BUILD_VERSION:-0.0.0}' \
        -o /source/publish/linux-arm64
    "

if [ $? -ne 0 ]; then
    echo "Docker build failed."
    exit 1
fi

# Copy deployment files (these operations are safe to do on host)
echo "Copying deployment files..."
cp 99-baird.rules "$OUTPUT_DIR/"
cp install_deps.sh "$OUTPUT_DIR/"
cp baird.service "$OUTPUT_DIR/"
cp install_service.sh "$OUTPUT_DIR/"
cp update_baird.sh "$OUTPUT_DIR/"
cp baird-updater.service "$OUTPUT_DIR/"
cp baird-updater.timer "$OUTPUT_DIR/"

# Verify AOT artifact creation
if [ -f "$OUTPUT_DIR/Baird" ]; then
  echo "✓ AOT binary 'Baird' successfully created via Docker"
  ls -lh "$OUTPUT_DIR/Baird"
else
  echo "✗ ERROR: AOT binary not found"
  exit 1
fi

echo "Build complete."
