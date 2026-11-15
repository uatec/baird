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
    libgl1-mesa-dri \
    libegl-mesa0 \
    libegl1 \
    libicu-dev \
    libdrm-tests

# libglapi-mesa was folded into other Mesa packages in Debian trixie; install it only where it
# still exists (bookworm) so set -e doesn't abort here.
if apt-cache show libglapi-mesa >/dev/null 2>&1; then
    apt-get install -y libglapi-mesa
fi

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

# --- 6. Wayland (labwc) session + HDMI colour range ---
# Baird runs under labwc (Raspberry Pi OS's default Wayland compositor) as an XWayland client:
# labwc presents every frame at the display's vblank, which removed the video tearing we had
# under X11 (this Xorg's modesetting driver has no TearFree option). The Xorg files above only
# matter if the session is switched back to X11 (rpd-x).
#
# Under labwc nothing sets the HDMI "Broadcast RGB" property, and the kernel default
# "Automatic" resolves to full range on our TV (washed-out picture — the same bug
# 20-hdmi-color.conf fixes for X11). baird-hdmi-range.service pins it to Limited at boot,
# while no compositor holds DRM master; the value then persists.
echo "[HDMI] Installing baird-hdmi-range boot service..."
cat > /usr/local/sbin/baird-hdmi-range.sh <<'HDMI_SCRIPT'
#!/bin/sh
# Managed by Baird configure_pi.sh — pins every HDMI output to limited RGB range (16-235)
# before the display server starts.
#
# Under labwc (wlroots) nothing sets the "Broadcast RGB" connector property, and the kernel
# default "Automatic" resolves to full range on our TV -> washed-out, "blobby" picture. Under X11
# xorg.conf.d/20-hdmi-color.conf did this; here we set it with modetest while no compositor holds
# DRM master. The value persists until something changes it. Set on all HDMI connectors, not just
# connected ones, so it still applies if the TV was off at boot. Runs after Plymouth (the boot
# splash holds DRM master until it quits) and before lightdm.
set -u
i=0
until modetest -M vc4 -c >/dev/null 2>&1; do # wait for the vc4 KMS driver
    i=$((i + 1))
    [ "$i" -ge 40 ] && { echo "vc4 DRM device not available" >&2; exit 1; }
    sleep 0.25
done
# Current "Broadcast RGB" value of a connector (0 Automatic, 1 Full, 2 Limited).
value_of() {
    modetest -M vc4 -c 2>/dev/null | awk -v id="$1" '
        $1 == id && $2 ~ /^[0-9]+$/ {inconn = 1; next}
        /^[0-9]+\t/ {inconn = 0}
        inconn && /Broadcast RGB/ {f = 1}
        inconn && f && /value:/ {print $2; exit}'
}

# modetest exits 0 even when the write is refused (e.g. another process still holds DRM
# master), so read the value back and retry for a few seconds.
status=0
for id in $(modetest -M vc4 -c 2>/dev/null | awk '$4 ~ /^HDMI-A-/ {print $1}'); do
    n=0
    while [ "$(value_of "$id")" != 2 ]; do
        n=$((n + 1))
        if [ "$n" -gt 40 ]; then
            echo "connector $id: could not set Broadcast RGB (value $(value_of "$id"))" >&2
            status=1
            break
        fi
        modetest -M vc4 -w "$id:Broadcast RGB:2" >/dev/null 2>&1
        sleep 0.25
    done
    [ "$(value_of "$id")" = 2 ] && echo "connector $id: Broadcast RGB = Limited 16:235"
done
exit "$status"
HDMI_SCRIPT
chmod 755 /usr/local/sbin/baird-hdmi-range.sh

cat > /etc/systemd/system/baird-hdmi-range.service <<'HDMI_UNIT'
# Managed by Baird configure_pi.sh
[Unit]
Description=Pin HDMI to limited RGB range (16-235) for Baird
# Needs DRM master, which the Plymouth boot splash holds until it quits, and must finish before
# the compositor takes it (the setting then persists). Plymouth only quits once the boot reaches
# the display manager, so waiting for it (plymouth-quit-wait) would deadlock: instead we quit the
# splash ourselves just before lightdm would anyway.
After=plymouth-start.service systemd-modules-load.service
Before=display-manager.service

[Service]
Type=oneshot
ExecStartPre=-/usr/bin/plymouth quit
ExecStart=/usr/local/sbin/baird-hdmi-range.sh
RemainAfterExit=yes

[Install]
WantedBy=graphical.target
HDMI_UNIT
systemctl daemon-reload
systemctl enable baird-hdmi-range.service
echo "[HDMI] baird-hdmi-range.service enabled."

LIGHTDM_CONF="/etc/lightdm/lightdm.conf"
if [ -f "$LIGHTDM_CONF" ]; then
    echo "[Session] Selecting the labwc (Wayland) session in $LIGHTDM_CONF..."
    sed -i -E 's/^(user-session|autologin-session)=.*/\1=rpd-labwc/' "$LIGHTDM_CONF"
    echo "[Session] Done (revert to X11 by setting these to rpd-x)."
else
    echo "[Session] $LIGHTDM_CONF not found — select the labwc session manually."
fi

# --- 7. Remote power/volume keys go to Baird, not the Pi ---
# The remote is a USB keyboard, so its power and volume buttons arrive as XF86PowerOff/XF86Sleep and
# XF86Audio* keys. Baird turns them into HDMI-CEC power/volume commands for the TV, but labwc binds
# them first (shutdown dialog, Pi volume) and logind acts on power/sleep keys. labwc runs with -m,
# so this user rc.xml merges over /etc/xdg/labwc/rc.xml; action "None" clears a default binding.
TARGET_USER="${SUDO_USER:-}"
if [ -n "$TARGET_USER" ] && [ "$TARGET_USER" != "root" ]; then
    LABWC_RC="$(getent passwd "$TARGET_USER" | cut -d: -f6)/.config/labwc/rc.xml"
    if [ ! -f "$LABWC_RC" ] || grep -q "Managed by Baird configure_pi.sh" "$LABWC_RC"; then
        echo "[Remote] Writing $LABWC_RC..."
        install -d -o "$TARGET_USER" -g "$TARGET_USER" "$(dirname "$LABWC_RC")"
        cat > "$LABWC_RC" <<'LABWC_RC_XML'
<?xml version="1.0"?>
<!-- Managed by Baird configure_pi.sh: leave the remote's power/volume keys to Baird (HDMI-CEC). -->
<labwc_config>
  <keyboard>
    <keybind key="XF86AudioRaiseVolume"><action name="None" /></keybind>
    <keybind key="XF86AudioLowerVolume"><action name="None" /></keybind>
    <keybind key="XF86AudioMute"><action name="None" /></keybind>
    <keybind key="XF86PowerOff" onRelease="yes"><action name="None" /></keybind>
  </keyboard>
  <!-- The Netflix spike's Chromium (window class baird-netflix, kiosk) opens at the bottom of the stack, behind Baird's fullscreen
       window, and is raised with xdotool windowactivate when playback starts. Don't add ignoreFocusRequest: it blocks the raise. -->
  <windowRules>
    <windowRule identifier="baird-netflix" skipTaskbar="yes" skipWindowSwitcher="yes"><action name="Lower"/></windowRule>
  </windowRules>
</labwc_config>
LABWC_RC_XML
        chown "$TARGET_USER:$TARGET_USER" "$LABWC_RC"
        # labwc ignores the whole file if it isn't well-formed XML (e.g. "--" inside a comment), and the
        # Pi's power dialog and volume keys silently come back.
        if ! python3 -c 'import sys, xml.dom.minidom; xml.dom.minidom.parse(sys.argv[1])' "$LABWC_RC"; then
            echo "[Remote] ERROR: $LABWC_RC is not valid XML — labwc will ignore it."
        fi
    else
        echo "[Remote] $LABWC_RC exists and isn't managed by Baird — add 'None' keybinds for"
        echo "         XF86AudioRaiseVolume/LowerVolume/Mute and XF86PowerOff to it by hand."
    fi
else
    echo "[Remote] Run via sudo from the desktop user to configure labwc key bindings."
fi

mkdir -p /etc/systemd/logind.conf.d
cat > /etc/systemd/logind.conf.d/baird-remote.conf <<'LOGIND_CONF'
# Managed by Baird configure_pi.sh — the remote's power/sleep keys switch the TV, not the Pi.
[Login]
HandlePowerKey=ignore
HandleSuspendKey=ignore
LOGIND_CONF
echo "[Remote] Done (takes effect after reboot)."

# --- 8. Boot splash: one picture from power-on until Baird's start-up animation ---
# The firmware rainbow goes; Plymouth shows the Baird wordmark on black (splash.png is the first frame of Baird's intro,
# rendered by its test); the desktop that shows between Plymouth and Baird is the same picture, taskbar hidden, no icons.
SPLASH_DIR="$(cd "$(dirname "$0")" && pwd)/boot-splash"
[ -d "$SPLASH_DIR" ] || SPLASH_DIR="$(cd "$(dirname "$0")" && pwd)/pi/boot-splash"
if [ -f "$SPLASH_DIR/splash.png" ]; then
    if ! grep -q "^disable_splash=1" "$BOOT_CONFIG"; then
        echo "[Splash] Turning off the firmware rainbow (disable_splash=1)..."
        # Before any [section] / os_prefix lines, so it applies to every boot.
        sed -i '0,/^\[/s//disable_splash=1\n[/' "$BOOT_CONFIG"
        grep -q "^disable_splash=1" "$BOOT_CONFIG" || sed -i '1i disable_splash=1' "$BOOT_CONFIG"
    fi
    if command -v plymouth-set-default-theme >/dev/null 2>&1; then
        install -d /usr/share/plymouth/themes/baird
        install -m 644 "$SPLASH_DIR/baird.plymouth" "$SPLASH_DIR/baird.script" "$SPLASH_DIR/splash.png" /usr/share/plymouth/themes/baird/
        plymouth-set-default-theme baird
        echo "[Splash] Plymouth theme: $(plymouth-set-default-theme)"
    else
        echo "[Splash] Plymouth isn't installed; skipping the boot theme."
    fi
    install -d /usr/share/baird
    install -m 644 "$SPLASH_DIR/splash.png" /usr/share/baird/splash.png
    if [ -n "$TARGET_USER" ] && [ "$TARGET_USER" != "root" ]; then
        USER_HOME="$(getent passwd "$TARGET_USER" | cut -d: -f6)"
        install -d -o "$TARGET_USER" -g "$TARGET_USER" "$USER_HOME/.config/pcmanfm/default" "$USER_HOME/.config/labwc"
        for n in 0 1; do
            cat > "$USER_HOME/.config/pcmanfm/default/desktop-items-$n.conf" <<'DESKTOP_CONF'
# Managed by Baird configure_pi.sh: the desktop is Baird's boot splash (seen only while Baird starts).
[*]
wallpaper_mode=center
wallpaper_common=1
wallpaper=/usr/share/baird/splash.png
desktop_bg=#000000000000
desktop_fg=#000000000000
desktop_shadow=#000000000000
show_documents=0
show_trash=0
show_mounts=0
DESKTOP_CONF
            chown "$TARGET_USER:$TARGET_USER" "$USER_HOME/.config/pcmanfm/default/desktop-items-$n.conf"
        done
        # The taskbar (started by the system's labwc autostart, which also runs) hides itself and shows no pop-ups.
        install -d -o "$TARGET_USER" -g "$TARGET_USER" "$USER_HOME/.config/wf-panel-pi"
        cat > "$USER_HOME/.config/wf-panel-pi/wf-panel-pi.ini" <<'PANEL'
# Managed by Baird configure_pi.sh: the taskbar stays out of sight (Baird is full screen; this is the boot gap).
[panel]
autohide=true
autohide_duration=0
notify_enable=false
PANEL
        chown -R "$TARGET_USER:$TARGET_USER" "$USER_HOME/.config/wf-panel-pi"
        # An earlier version wrote a user labwc autostart; labwc runs it as well as the system one (pcmanfm twice).
        if grep -q "Managed by Baird" "$USER_HOME/.config/labwc/autostart" 2>/dev/null; then rm -f "$USER_HOME/.config/labwc/autostart"; fi
    fi
    echo "[Splash] Done (takes effect after reboot)."
else
    echo "[Splash] boot-splash files not found next to this script; skipping."
fi

# --- 9. Let Baird restart TvHeadend when the DVB stick wedges (live TV "No input detected") ---
# Exactly one command, no password; Baird runs `sudo -n systemctl restart tvheadend` (TunerRecovery).
if [ -n "$TARGET_USER" ] && [ "$TARGET_USER" != "root" ]; then
    SUDOERS_TMP="$(mktemp)"
    cat > "$SUDOERS_TMP" <<SUDOERS_CONF
# Managed by Baird configure_pi.sh
$TARGET_USER ALL=(root) NOPASSWD: /usr/bin/systemctl restart tvheadend
SUDOERS_CONF
    if visudo -cf "$SUDOERS_TMP" >/dev/null; then
        install -m 440 -o root -g root "$SUDOERS_TMP" /etc/sudoers.d/baird-tvheadend
        echo "[Tuner] $TARGET_USER may restart tvheadend without a password."
    else
        echo "[Tuner] ERROR: generated sudoers rule is invalid; not installed."
    fi
    rm -f "$SUDOERS_TMP"
fi

echo ""
echo "=== Configuration complete ==="
echo "Please reboot the Raspberry Pi for all changes to take effect:"
echo "  sudo reboot"
