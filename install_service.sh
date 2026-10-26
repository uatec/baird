#!/bin/bash
# Installs/refreshes the Baird systemd user units from this package and
# (re)starts them. Run by update_baird.sh after every upgrade/rollback, and
# by hand for first-time setup.
set -euo pipefail

INSTALL_DIR="$HOME/.config/systemd/user"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

UNITS=(baird.service baird-updater.service baird-updater.timer)

mkdir -p "$INSTALL_DIR"

# 1. Copy every unit first...
for unit in "${UNITS[@]}"; do
    echo "Installing $unit..."
    cp "$SCRIPT_DIR/$unit" "$INSTALL_DIR/"
done

# 2. ...then reload once so systemd sees all of the new definitions before
#    anything is enabled or restarted.
systemctl --user daemon-reload

# 3. Enable and (re)start the app and the updater timer. The updater service
#    itself is not enabled; the timer triggers it.
# Automatic updates can be paused: touch ~/.baird/auto-update-paused (updates on demand, e.g. Settings → Update,
# still work, and leave the timer off).
PAUSED_MARKER="$HOME/.baird/auto-update-paused"
for unit in baird.service baird-updater.timer; do
    if [ "$unit" = baird-updater.timer ] && [ -e "$PAUSED_MARKER" ]; then
        systemctl --user disable --now "$unit" 2>/dev/null || true
        echo "Automatic updates are paused ($PAUSED_MARKER): $unit left off."
        continue
    fi
    systemctl --user enable "$unit"
    # Clear any start-limit-hit / failed state, otherwise restart is refused.
    systemctl --user reset-failed "$unit" 2>/dev/null || true
    systemctl --user restart "$unit"
    echo "$unit installed and restarted."
    echo "Status: systemctl --user status $unit"
    echo "----------------------------------------"
done

# 4. Install/refresh the yt-dlp mpv uses (non-fatal).
"$SCRIPT_DIR/update_ytdlp.sh"

echo "All services installed successfully."
