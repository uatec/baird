#!/bin/bash
# configure_pi.sh
# One-time Raspberry Pi OS setup script for Baird.
# Run this script on the Pi before starting Baird for the first time:
#
#   chmod +x configure_pi.sh
#   sudo ./configure_pi.sh
#
# Tested on Raspberry Pi OS (64-bit) on Raspberry Pi 5.

set -e

if [ "$EUID" -ne 0 ]; then
    echo "Error: this script must be run as root (use sudo ./configure_pi.sh)"
    exit 1
fi

BOOT_CONFIG="/boot/config.txt"
# Raspberry Pi OS Bookworm moved config to /boot/firmware/config.txt
if [ -f /boot/firmware/config.txt ]; then
    BOOT_CONFIG="/boot/firmware/config.txt"
fi

echo "=== Baird Raspberry Pi Configuration ==="
echo "Using boot config: $BOOT_CONFIG"

# --- 1. Enable Full KMS (vc4-kms-v3d) ---
# Full KMS is required for DRM/KMS direct rendering and proper OpenGL support.
# If the legacy fake-KMS overlay is present, replace it; otherwise add Full KMS.
if grep -q "dtoverlay=vc4-kms-v3d" "$BOOT_CONFIG"; then
    echo "[KMS] Full KMS already enabled — skipping."
elif grep -q "dtoverlay=vc4-fkms-v3d" "$BOOT_CONFIG"; then
    echo "[KMS] Replacing legacy fake-KMS with Full KMS..."
    sed -i 's/dtoverlay=vc4-fkms-v3d/dtoverlay=vc4-kms-v3d/' "$BOOT_CONFIG"
else
    echo "[KMS] Adding Full KMS overlay..."
    echo "dtoverlay=vc4-kms-v3d" >> "$BOOT_CONFIG"
fi

# --- 2. Install required libraries ---
echo "[APT] Installing required packages..."
apt-get update -qq
apt-get install -y \
    libmpv-dev \
    libdrm2 \
    libdrm-dev \
    libgbm1 \
    libgbm-dev \
    libinput10 \
    libglapi-mesa \
    libgl1-mesa-dri \
    libegl-mesa0 \
    libegl1 \
    libicu-dev

echo "[APT] Packages installed."

# --- 3. Grant the current user access to DRI and input devices ---
# udev rules are copied separately; here we ensure group membership.
REAL_USER="${SUDO_USER:-$USER}"
if [ -n "$REAL_USER" ] && id "$REAL_USER" &>/dev/null; then
    echo "[Groups] Adding $REAL_USER to video and input groups..."
    usermod -aG video,input "$REAL_USER"
    echo "[Groups] Done. Log out and back in for group changes to take effect."
fi

# --- 4. System-level video playback optimizations ---
# Increase CMA (Contiguous Memory Allocator) for the GPU if not already set.
# 256 MB is a good default for 1080p video; increase to 512 for 4K.
if ! grep -q "^cma=" "$BOOT_CONFIG" && ! grep -q "^dtoverlay=.*cma" "$BOOT_CONFIG"; then
    echo "[CMA] Setting GPU memory to 256 MB..."
    echo "cma=256M" >> "$BOOT_CONFIG"
else
    echo "[CMA] GPU memory already configured — skipping."
fi

# Disable screen blanking/DPMS so the display stays on during playback.
CMDLINE_FILE="/boot/cmdline.txt"
[ -f /boot/firmware/cmdline.txt ] && CMDLINE_FILE="/boot/firmware/cmdline.txt"
if ! grep -q "consoleblank=0" "$CMDLINE_FILE" 2>/dev/null; then
    echo "[DPMS] Disabling console blanking..."
    # /boot/cmdline.txt is a single line; append the parameter safely.
    sed -i 's/$/ consoleblank=0/' "$CMDLINE_FILE"
else
    echo "[DPMS] Console blanking already disabled — skipping."
fi

echo ""
echo "=== Configuration complete ==="
echo "Please reboot the Raspberry Pi for all changes to take effect:"
echo "  sudo reboot"
