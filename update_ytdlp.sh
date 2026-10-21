#!/bin/bash
# Installs, or self-updates, the yt-dlp that Baird's mpv uses for YouTube (and as the
# iPlayer fallback). Run by install_service.sh on every deploy and by update_baird.sh
# hourly, because YouTube breaks yt-dlp far more often than Baird ships.
#
# Distro packages lag badly (Debian trixie froze at 2025.04.30, which YouTube broke),
# so Baird keeps its own copy of the upstream standalone build. MpvPlayer points mpv's
# ytdl hook at this path when it exists.
#
# Never fails the caller: a network blip must not break a deploy.
set -uo pipefail

DEST="${XDG_DATA_HOME:-$HOME/.local/share}/baird/bin/yt-dlp"

case "$(uname -m)" in
aarch64 | arm64) ASSET=yt-dlp_linux_aarch64 ;;
x86_64) ASSET=yt-dlp_linux ;;
*)
    echo "yt-dlp: unsupported architecture $(uname -m); skipping."
    exit 0
    ;;
esac

if [[ -x "$DEST" ]]; then
    "$DEST" -U || echo "WARNING: yt-dlp self-update failed; keeping $("$DEST" --version 2>/dev/null)."
    exit 0
fi

mkdir -p "$(dirname "$DEST")"
tmp="$(mktemp "$DEST.XXXXXX")"
if curl -fsSL --max-time 120 -o "$tmp" "https://github.com/yt-dlp/yt-dlp/releases/latest/download/$ASSET"; then
    chmod 755 "$tmp"
    mv "$tmp" "$DEST"
    echo "Installed yt-dlp $("$DEST" --version) to $DEST."
else
    rm -f "$tmp"
    echo "WARNING: could not download yt-dlp; mpv will fall back to any yt-dlp on PATH."
fi
exit 0
