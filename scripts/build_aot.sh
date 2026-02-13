#!/bin/bash
set -e

# Detect OS
if [[ "$OSTYPE" == "darwin"* ]]; then
    # On macOS, check if Docker is running
    if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
        echo "Detected macOS. Delegating to Docker build..."
        source ./scripts/build_aot_docker.sh
        exit $?
    else
        echo "Warning: Building on macOS without Docker for Linux AOT target is NOT supported and will fail."
        echo "Please install Docker Desktop or run on Linux."
        read -p "Attempt build anyway? (y/N) " confirm
        if [[ "$confirm" != "y" ]]; then
            exit 1
        fi
    fi
fi

# Fallback to standard local build (works on Linux)

# Default version if not set
BUILD_VERSION="${BUILD_VERSION:-0.0.0}"

echo "Starting AOT Build script..."
echo "Build Version: ${BUILD_VERSION}"

# Install dependencies if on Linux and missing
# Install dependencies only if we suspect we are on a bare VM/user machine where sudo is available and clang is missing.
# In CI container, dependencies are pre-installed or installed via YAML step.
if [ -f /etc/os-release ]; then
    . /etc/os-release
    if [[ "$ID" == "ubuntu" || "$ID" == "debian" ]]; then
        if ! command -v clang >/dev/null; then
             echo "Clang not found. Attempting install (will fail without sudo permissions if not root)..."
             if [ "$(id -u)" -eq 0 ]; then
                apt-get update && apt-get install -y clang zlib1g-dev
             elif command -v sudo >/dev/null; then
                sudo apt-get update && sudo apt-get install -y clang zlib1g-dev
             else
                echo "Warning: clang missing and cannot install (no sudo). Build may fail."
             fi
        fi
    fi
fi

echo "Cleaning previous builds..."
dotnet clean Baird/Baird.csproj
rm -rf ./publish

# Build for linux-arm64 with AOT compilation
echo "Building for linux-arm64 with AOT..."
dotnet publish Baird/Baird.csproj \
  -c Release \
  -r linux-arm64 \
  --self-contained true \
  -p:PublishAot=true \
  -p:PublishTrimmed=true \
  -p:Version="${BUILD_VERSION}" \
  -o ./publish/linux-arm64

if [ $? -ne 0 ]; then
    echo "Build failed."
    exit 1
fi

# Copy deployment files
echo "Copying deployment files..."
cp 99-baird.rules ./publish/linux-arm64/ || true
cp install_deps.sh ./publish/linux-arm64/ || true
cp baird.service ./publish/linux-arm64/ || true
cp install_service.sh ./publish/linux-arm64/ || true
cp update_baird.sh ./publish/linux-arm64/ || true
cp baird-updater.service ./publish/linux-arm64/ || true
cp baird-updater.timer ./publish/linux-arm64/ || true

# Verify AOT artifact creation
if [ -f "./publish/linux-arm64/Baird" ]; then
  echo "✓ AOT binary 'Baird' successfully created"
  ls -lh ./publish/linux-arm64/Baird
else
  echo "✗ ERROR: AOT binary not found"
  exit 1
fi

echo "Build complete."
