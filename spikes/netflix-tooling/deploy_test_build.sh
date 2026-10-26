#!/bin/bash
# Runs ON THE DEV PC from the repo root. Publishes the linux-arm64 build exactly as CI does and copies it to ~/baird-test
# on the Pi (beside the brew install, which is untouched), plus the Pi-side scripts. Takes ~5 min (trimming).
#   usage: spikes/netflix-tooling/deploy_test_build.sh
set -euo pipefail
OUT=/tmp/baird-arm64-test
rm -rf "$OUT"
dotnet publish Baird/Baird.csproj -c Release -r linux-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:Version=0.0.999-autotest -o "$OUT" | grep -E "error|Baird ->" || true
ssh -o HostKeyAlias=baird baird.local 'mkdir -p ~/baird-test'
scp -q -o HostKeyAlias=baird "$OUT"/Baird "$OUT"/libSkiaSharp.so "$OUT"/libHarfBuzzSharp.so "$OUT"/librive.so \
  spikes/netflix-tooling/pi_autotest.sh spikes/netflix-tooling/pi_harness.sh spikes/netflix-tooling/pi_keys.py baird.local:baird-test/
echo "Deployed. Run e.g.: ssh -o HostKeyAlias=baird baird.local 'bash ~/baird-test/pi_autotest.sh netflix-core test'"
