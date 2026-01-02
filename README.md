# Baird

An Avalonia UI application for Linux Framebuffer (Raspberry Pi).

## Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Building for Deployment
Run the build script to create self-contained binaries for Raspberry Pi (Linux ARM64) and Generic Linux (x64):
```bash
./build.sh
```
Artifacts are created in `publish/`.

## Deployment

### Raspberry Pi (Linux ARM64)
1. **Transfer files:**
   ```bash
   scp publish/linux-arm64/Baird user@raspberrypi:/home/user/
   scp publish/linux-arm64/99-baird.rules user@raspberrypi:/home/user/
   scp publish/linux-arm64/install_deps.sh user@raspberrypi:/home/user/
   ```

2. **Install Dependencies (One-time setup):**
    ```bash
    chmod +x install_deps.sh
    sudo ./install_deps.sh
    ```

3. **Configure Permissions (One-time setup):**
   On the Pi, install udev rules to allow non-root access to `/dev/dri/card0` and input devices:
   ```bash
   sudo cp 99-baird.rules /etc/udev/rules.d/
   sudo udevadm control --reload-rules && sudo udevadm trigger
   sudo usermod -aG video,input $USER
   # Logout and login again for group changes to take effect
   ```

3. **Run Application:**
   ```bash
   chmod +x Baird
   ./Baird
   ```
   *Note: Ensure no other display server (X11/Wayland) is holding the DRM device if running in framebuffer mode.*

### Linux VM (x64)
1. **Transfer files:**
   ```bash
   scp publish/linux-x64/Baird user@vm:/home/user/
   ```

2. **Run Application:**
   ```bash
   chmod +x Baird
   ./Baird
   ```

## Smoke Testing

A simple smoke test strategy to verify the application is running and consuming resources.

### On Device (Pi/VM)

1. **Start the application in background:**
   ```bash
   ./Baird &
   PID=$!
   ```

2. **Verify Process:**
   Check if the process is still running after a few seconds:
   ```bash
   sleep 5
   ps -p $PID
   ```

3. **Verify GPU Usage (Raspberry Pi/Intel):**
   Install `IGT GPU Tools` (if available for your platform):
   ```bash
   sudo apt-get install intel-gpu-tools  # or platform equivalent
   sudo intel_gpu_top
   ```
   *Look for the `Baird` process in the client list.*

4. **Verify Input/Output:**
   - Ensure the "Hello World" text is visible on the connected display.
   - Click the "Exit" button to verify input handling and graceful shutdown.
