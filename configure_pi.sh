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

# --- 5. Xorg configuration for the V3D GPU and display ---
# These files were previously created by hand on the Pi and lived outside the
# repo. They are managed here so a freshly flashed Pi is fully reproducible.
XORG_CONF_DIR="/etc/X11/xorg.conf.d"
echo "[Xorg] Writing Xorg configuration to $XORG_CONF_DIR..."
mkdir -p "$XORG_CONF_DIR"

# 5a. Disable X DPMS and screen blanking so the display stays on during playback.
# Complements the console-level consoleblank=0 set above (this covers X11).
cat > "$XORG_CONF_DIR/10-blanking.conf" <<'EOF'
# Managed by Baird configure_pi.sh — keep the display on during playback.
Section "Extensions"
    Option      "DPMS" "Disable"
EndSection

Section "ServerLayout"
    Identifier "ServerLayout0"
    Option "StandbyTime" "0"
    Option "SuspendTime" "0"
    Option "OffTime"     "0"
    Option "BlankTime"   "0"
EndSection
EOF
echo "[Xorg]   10-blanking.conf written."

# 5b. Force HDMI limited (broadcast) RGB range.
# TVs expect limited RGB (16-235). With "Automatic", the modesetting driver may
# output full range (0-255), which the TV then re-compresses — washed-out, soft,
# "blobby" image. Pinning Limited 16:235 matches what set-top boxes/Chromecast
# send and restores correct contrast and sharpness. Applies to all HDMI outputs.
cat > "$XORG_CONF_DIR/20-hdmi-color.conf" <<'EOF'
# Managed by Baird configure_pi.sh — forces limited (broadcast) RGB range so the
# TV receives 16-235 and does not re-compress a full-range signal.
Section "Monitor"
    Identifier "HDMI-1"
    Option "Broadcast RGB" "Limited 16:235"
EndSection

Section "Monitor"
    Identifier "HDMI-2"
    Option "Broadcast RGB" "Limited 16:235"
EndSection
EOF
echo "[Xorg]   20-hdmi-color.conf written."

# 5c. Use the modesetting driver on the VideoCore (vc4) GPU as the primary GPU.
# Required for correct OpenGL rendering of the mpv video surface on the Pi 5.
cat > "$XORG_CONF_DIR/99-v3d.conf" <<'EOF'
# Managed by Baird configure_pi.sh — modesetting on vc4 as the primary GPU.
Section "OutputClass"
  Identifier "vc4"
  MatchDriver "vc4"
  Driver "modesetting"
  Option "PrimaryGPU" "true"
EndSection
EOF
echo "[Xorg]   99-v3d.conf written."
echo "[Xorg] Done (takes effect on next X restart/reboot)."

echo ""
echo "=== Configuration complete ==="
echo "Please reboot the Raspberry Pi for all changes to take effect:"
echo "  sudo reboot"
