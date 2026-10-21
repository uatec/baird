#!/usr/bin/env bash
# Runner for Baird Spike 3: Rive Web View Spike

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "=== Baird Spike 3: Rive Web View Spike ==="

# Check Python 3
if command -v python3 >/dev/null 2>&1; then
    exec python3 serve.py "$@"
elif command -v node >/dev/null 2>&1; then
    echo "Python3 not found, using npx serve..."
    exec npx serve -l 8088 .
else
    echo "Opening index.html directly in browser..."
    if command -v xdg-open >/dev/null 2>&1; then
        xdg-open index.html
    else
        echo "Please open $SCRIPT_DIR/index.html in your web browser."
    fi
fi
