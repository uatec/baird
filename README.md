# Baird

An Avalonia UI application for Linux Framebuffer (Raspberry Pi).

## Prerequisites
- [.NET 9 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/9.0)

## Configuration

Baird uses standard Microsoft.Extensions.Configuration for settings, prioritizing INI files. You can configure the application using:

1.  **`config.ini`**: Create a file named `config.ini` in the application directory OR in `~/.baird/config.ini`. See `config.example.ini` for a template.
2.  **Environment Variables**: Set environment variables matching the configuration keys.

### Key Configuration Options

| Key | Description | Default |
| :--- | :--- | :--- |
| `TVH_URL` | URL for TvHeadend server | `http://localhost:9981` |
| `TVH_USER` | TvHeadend username | `unknown` |
| `TVH_PASS` | TvHeadend password | `unknown` |
| `JELLYFIN_URL` | URL for Jellyfin server | `http://localhost:8096` |
| `JELLYFIN_USER` | Jellyfin username | `unknown` |
| `JELLYFIN_PASS` | Jellyfin password | `unknown` |
| `BAIRD_FULLSCREEN` | Set to `true` to force fullscreen mode | `false` |

## Testing on macOS
While the production environment runs on Linux as a fullscreen framebuffer application, you can test the application on macOS in desktop mode.

### Setup for macOS Testing
1. **Install Homebrew** (if not already installed):
   ```bash
   /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
   ```

2. **Install Dependencies:**
   ```bash
   chmod +x install_deps.sh
   ./install_deps.sh
   ```
   This will automatically detect macOS and install libmpv via Homebrew.

3. **Run Application:**
   ```bash
   dotnet run --project Baird/Baird.csproj
   ```
   The application will automatically run in desktop mode on macOS.

**Note:** The application uses platform-specific libmpv bindings (`libmpv.2.dylib` on macOS, `libmpv.so.2` on Linux), so the same codebase works for both testing and production.

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
