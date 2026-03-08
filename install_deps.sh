#!/bin/bash
set -e

echo "Installing Baird dependencies..."

# Detect OS
OS="$(uname -s)"

case "${OS}" in
    Darwin*)
        echo "Detected macOS - using Homebrew"
        
        # Check if Homebrew is installed
        if ! command -v brew &> /dev/null; then
            echo "Error: Homebrew is not installed. Please install it from https://brew.sh"
            exit 1
        fi
        
        echo "Installing mpv and audio recording tools..."
        brew install mpv sox
        
        echo ""
        echo "NOTE: For voice search, place a Whisper GGML model file and set"
        echo "      WHISPER_MODEL_PATH in your config.ini."
        echo "      Download from: https://huggingface.co/ggerganov/whisper.cpp/tree/main"
        echo "      e.g. ggml-base.en.bin is a good starting point."
        
        echo "Dependencies installed successfully on macOS."
        ;;
        
    Linux*)
        echo "Detected Linux - using apt-get"
        
        if [ "$EUID" -ne 0 ]; then 
            echo "Please run as root (sudo ./install_deps.sh)"
            exit 1
        fi
        
        apt-get update
        apt-get install -y \
            libgbm1 \
            libdrm2 \
            libinput10 \
            libglapi-mesa \
            libgl1-mesa-dri \
            libegl-mesa0 \
            libegl1 \
            libicu-dev \
            libmpv-dev \
            alsa-utils
            # libmpv1 \
        
        echo ""
        echo "NOTE: For voice search, place a Whisper GGML model file and set"
        echo "      WHISPER_MODEL_PATH in your config.ini."
        echo "      Download from: https://huggingface.co/ggerganov/whisper.cpp/tree/main"
        echo "      e.g. ggml-base.en.bin is a good starting point."
        
        echo "Dependencies installed successfully on Linux."
        ;;
        
    *)
        echo "Unsupported operating system: ${OS}"
        exit 1
        ;;
esac

