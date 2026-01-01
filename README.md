# Baird

A simple .NET 8 Hello World console application setup for cross-platform deployment.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)

## Getting Started

1. **Clone the repository:**
   ```bash
   git clone <repository_url>
   cd baird
   ```

2. **Run locally:**
   ```bash
   dotnet run --project Baird/Baird.csproj
   ```

## Building for Deployment

A `build.sh` script is provided to create self-contained native binaries for Raspberry Pi (Linux ARM64) and generic Linux VMs (Linux x64).

1. **Run the build script:**
   ```bash
   ./build.sh
   ```

2. **Locate artifacts:**
   Build artifacts will be available in the `publish/` directory:
   - **Raspberry Pi:** `publish/linux-arm64/Baird`
   - **Linux VM:** `publish/linux-x64/Baird`

## Deployment

### Raspberry Pi
Copy the binary to your Raspberry Pi:
```bash
scp publish/linux-arm64/Baird user@raspberrypi:/home/user/
```

### Linux VM
Copy the binary to your VM:
```bash
scp publish/linux-x64/Baird user@vm:/home/user/
```

### Running on Target
Allow execution and run:
```bash
chmod +x Baird
./Baird
```
