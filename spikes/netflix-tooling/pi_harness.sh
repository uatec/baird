#!/bin/bash
# Runs ON THE PI. Starts a Chromium with Baird's Netflix flags, CDP on port 9557, kept behind Baird (focus handed back),
# for hand-driven experiments with cdpmini.py through an ssh tunnel. Stop with: systemctl --user stop baird-probe.service
#   usage: pi_harness.sh <profile-dir> [url]
# Use a COPY of the real profile for anything risky (cp -r ~/.config/baird/browser-profile /tmp/x; rm /tmp/x/Singleton*),
# and never run it at the same time as Baird's own Netflix browser on the same profile (Chromium's profile lock).
set -u
PROFILE=$1; URL=${2:-https://www.netflix.com/watch/80107105}
export XDG_RUNTIME_DIR=/run/user/$(id -u)
systemctl --user stop baird-probe.service 2>/dev/null; sleep 2
prev=$(DISPLAY=:0 xdotool getactivewindow)
systemd-run --user --unit=baird-probe --collect --setenv=DISPLAY=:0 --setenv=XAUTHORITY=$HOME/.Xauthority \
  /usr/bin/chromium --remote-debugging-port=9557 --ozone-platform=x11 --kiosk --deny-permission-prompts --window-size=1920,1080 \
  --user-data-dir="$PROFILE" --autoplay-policy=no-user-gesture-required --no-first-run --no-default-browser-check --password-store=basic \
  --disable-session-crashed-bubble --class=baird-netflix "$URL" >/dev/null 2>&1
sleep 5; DISPLAY=:0 xdotool windowactivate "$prev" 2>/dev/null
echo "harness chromium up on port 9557 (profile $PROFILE), behind Baird"
