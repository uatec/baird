#!/bin/bash

# Define the service name and file
SERVICE_NAME="baird.service"
SERVICE_FILE="baird.service"
INSTALL_DIR="$HOME/.config/systemd/user"
SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

# Ensure the systemd user directory exists
mkdir -p "$INSTALL_DIR"

# Copy the service file to the install directory
cp "$SCRIPT_DIR/$SERVICE_FILE" "$INSTALL_DIR/$SERVICE_NAME"

# Reload systemd manager configuration
systemctl --user daemon-reload

# Enable and start the service
systemctl --user enable "$SERVICE_NAME"
systemctl --user restart "$SERVICE_NAME"

echo "Baird service installed and started successfully."
echo "You can check the status with: systemctl --user status $SERVICE_NAME"
