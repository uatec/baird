#!/bin/bash

# Smoke Test for Baird
# Supports TARGET_PLATFORM environment variable (defaults to linux-arm64)

TARGET_PLATFORM="${TARGET_PLATFORM:-linux-arm64}"

echo "Starting Smoke Test for ${TARGET_PLATFORM}..."

# 1. Check if we can run "dry" or just check build output?
# Since we can't easily run the UI on this agent without display/drm, 
# we will verify the binary exists.
if [ ! -f "publish/${TARGET_PLATFORM}/Baird" ]; then
    echo "FAIL: Binary not found in publish/${TARGET_PLATFORM}/"
    exit 1
fi

echo "PASS: Binary exists at publish/${TARGET_PLATFORM}/Baird"

# 2. Check for GPU monitoring tools presence (simulation of check)
if command -v instel_gpu_top &> /dev/null; then
    echo "INFO: intel_gpu_top is available."
else
    echo "WARN: intel_gpu_top not found (Expected on Pi if using verify tools, but okay here)."
fi

# 3. Simulate process check (Pseudo-code for what would run on device)
echo "INFO: To verify on device, run:"
echo "      ./Baird &"
echo "      pid=\$!"
echo "      sleep 5"
echo "      if ps -p \$pid > /dev/null; then echo 'PASS: Process running'; else echo 'FAIL: Process died'; fi"
echo "      kill \$pid"

echo "Smoke Test (Local Check) Complete."
