#!/bin/bash
set -e

# Ensure brew is in the path
export PATH="/home/linuxbrew/.linuxbrew/bin:$PATH"

echo "Checking for updates..."
brew update

# Check if baird is outdated
if [ -n "$(brew outdated --quiet uatec/tools/baird)" ]; then
    echo "Update available for baird. Upgrading..."
    brew upgrade uatec/tools/baird
    
    echo "Running install_service.sh from the new version..."
    # Execute the install script from the opt directory (stable link to latest version)
    /home/linuxbrew/.linuxbrew/opt/baird/libexec/install_service.sh
else
    echo "Baird is already up to date."
fi
