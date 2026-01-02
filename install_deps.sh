#!/bin/bash
set -e

echo "Installing Baird dependencies..."

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
    libmpv1 \
    libmpv-dev

echo "Dependencies installed."
