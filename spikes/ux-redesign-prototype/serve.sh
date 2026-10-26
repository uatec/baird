#!/usr/bin/env bash
# Serves the prototype on the Tailscale IP so it can be opened from the PC or the Pi.
cd "$(dirname "$0")"
HOST="${HOST:-100.109.40.12}"
PORT="${PORT:-8077}"
echo "Baird prototype: http://$HOST:$PORT/   (on the Pi, add ?lite if auto-detection misses it)"
exec python3 tools/serve.py "$HOST" "$PORT"
