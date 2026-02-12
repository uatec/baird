#!/bin/bash

INSTALL_DIR="$HOME/.config/systemd/user"
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

# Ensure the systemd user directory exists
mkdir -p "$INSTALL_DIR"

function install_unit() {
    local unit_file=$1
    local unit_name=$(basename "$unit_file")

    echo "Installing $unit_name..."
    cp "$SCRIPT_DIR/$unit_file" "$INSTALL_DIR/"
    systemctl --user enable "$unit_name"
    systemctl --user restart "$unit_name"
    echo "$unit_name installed and restarted."
    echo "Status: systemctl --user status $unit_name"
    echo "----------------------------------------"
}

# Install components
# Note: We copy the service file for the updater but don't enable/start it directly; 
# the timer handles that.
cp "$SCRIPT_DIR/baird-updater.service" "$INSTALL_DIR/"

# Reload systemd manager configuration to pick up new files
systemctl --user daemon-reload

# Install and start the main service and the updater timer
install_unit "baird.service"
install_unit "baird-updater.timer"

echo "All services installed successfully."
