#!/bin/bash
# Signs Baird in to Channel 4, once, by hand, from YOUR COMPUTER (not the Pi). The usual way is on the TV: play a Channel 4
# programme and press OK on "Channel 4 is not signed in"; this script is for when typing on the TV is awkward.
#
# Channel 4's sign-in page refuses automated browsers (reCAPTCHA Enterprise, Akamai), so Baird never types the password. Instead
# this opens Chromium ON THE PI, with Baird's own browser profile, but shows its window on THIS computer (ssh X forwarding). You
# sign in there with your own keyboard; the session is saved in Baird's profile on the Pi, and Baird plays Channel 4 with it.
# Run it again when Baird says Channel 4 is not signed in any more.
#
# Baird's browser uses the same profile and Chromium allows one process per profile, so baird.service is stopped while the window
# is open and started again when you close it.
#   usage: ./channel4_signin.sh [ssh-host]      (default: baird.local, as in the other Baird scripts)
set -euo pipefail
HOST=${1:-baird.local}
SSH=(ssh -X -o HostKeyAlias=baird "$HOST")
URL='https://www.channel4.com/sign-in-or-register?redirectUrl=https%3A%2F%2Fwww.channel4.com%2F'

echo "Stopping Baird on $HOST while you sign in..."
"${SSH[@]}" 'systemctl --user stop baird.service'
trap 'echo "Starting Baird again..."; "${SSH[@]}" "systemctl --user start baird.service" && echo "Baird is running."' EXIT

# Baird's Chromium may take a moment to exit and release the profile.
"${SSH[@]}" 'for i in $(seq 1 20); do pgrep -u "$USER" -f "user-data-dir=$HOME/.config/baird/browser-profile" >/dev/null || exit 0; sleep 0.5; done; echo "Baird'"'"'s browser is still running." >&2; exit 1'

echo "Opening Channel 4's sign-in page in a window from the Pi. Sign in, then close the window."
# The same profile and password store as Baird's own browser (CdpBrowserService.LaunchAsync), so Baird can read the session.
"${SSH[@]}" "chromium --ozone-platform=x11 --user-data-dir=\$HOME/.config/baird/browser-profile --password-store=basic \
  --no-first-run --no-default-browser-check --disable-session-crashed-bubble --new-window '$URL'" 2>/dev/null || true
