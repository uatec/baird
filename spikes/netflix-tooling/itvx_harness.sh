#!/bin/bash
# Runs ON THE PI: pi_harness.sh with audio muted, on a fresh copy of Baird's browser profile, for ITVX exploration.
set -u
export XDG_RUNTIME_DIR=/run/user/$(id -u)
PROFILE=/tmp/itvx-probe-profile
systemctl --user stop baird-probe.service 2>/dev/null; systemctl --user reset-failed baird-probe.service 2>/dev/null; sleep 2
rm -rf "$PROFILE"; cp -r ~/.config/baird/browser-profile "$PROFILE"; rm -f "$PROFILE"/Singleton*
du -sh "$PROFILE"
prev=$(DISPLAY=:0 xdotool getactivewindow)
systemd-run --user --unit=baird-probe --collect --setenv=DISPLAY=:0 --setenv=XAUTHORITY=$HOME/.Xauthority \
  /usr/bin/chromium --remote-debugging-port=9557 --ozone-platform=x11 --kiosk --deny-permission-prompts --window-size=1920,1080 \
  --user-data-dir="$PROFILE" --autoplay-policy=no-user-gesture-required --no-first-run --no-default-browser-check --password-store=basic \
  --disable-session-crashed-bubble --mute-audio --class=baird-netflix "${1:-https://www.itv.com/watch/vera/mz0gq64}" >/dev/null 2>&1
sleep 5; DISPLAY=:0 xdotool windowactivate "$prev" 2>/dev/null
echo "probe chromium up on 9557"
