#!/bin/bash
# X11 baseline for the Wayland spike: plays the same source with standalone mpv on the
# current X11 session (DISPLAY=:0) and reports the same numbers as run_test.sh, so the
# labwc results can be compared with what the Pi does today.
#
# Usage: ./x11_baseline.sh --channel "BBC One NE HD" [--gpu-api opengl|vulkan] [--seconds 60]
#        [--video-sync MODE]   (stops baird.service while running)
set -euo pipefail

SECONDS_PER_RUN=60
GPU_API="opengl"
VIDEO_SYNC=""
CHANNEL=""
EXTRA=""        # extra mpv options, e.g. "--profile=fast"
LABEL=""
while [[ $# -gt 0 ]]; do
    case "$1" in
    --channel) CHANNEL="$2"; shift 2 ;;
    --gpu-api) GPU_API="$2"; shift 2 ;;
    --video-sync) VIDEO_SYNC="$2"; shift 2 ;;
    --seconds) SECONDS_PER_RUN="$2"; shift 2 ;;
    --extra) EXTRA="$2"; shift 2 ;;
    --label) LABEL="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 1 ;;
    esac
done
[[ -n "$CHANNEL" ]] || { echo "need --channel" >&2; exit 1; }

ini_get() { sed -nE "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*(.*)$/\1/p" "$HOME/.baird/config.ini" | tail -n1 | tr -d '\r'; }
base="$(ini_get TVH_URL)"; base="${base%/}"; user="$(ini_get TVH_USER)"; pass="$(ini_get TVH_PASS)"
uuid="$(curl -fsS --digest -u "$user:$pass" "$base/api/channel/grid?limit=9999" |
    jq -r --arg n "$CHANNEL" '.entries[] | select((.name | ascii_downcase) == ($n | ascii_downcase)) | .uuid' | head -n1)"
url="$base/stream/channel/$uuid?auth=$(jq -rn --arg s "$user" '$s|@uri'):$(jq -rn --arg s "$pass" '$s|@uri')&profile=pass"

PROPS=(avsync frame-drop-count decoder-frame-drop-count mistimed-frame-count vo-delayed-frame-count display-sync-active current-vo hwdec-current)
query() {
    local i req=""
    for i in "${!PROPS[@]}"; do req+="{\"command\":[\"get_property\",\"${PROPS[$i]}\"],\"request_id\":$i}"$'\n'; done
    printf '%s' "$req" | socat -t 1 - "UNIX-CONNECT:$SOCK" 2>/dev/null |
        jq -s --argjson n "$(printf '%s\n' "${PROPS[@]}" | jq -R . | jq -s .)" \
            'map(select(has("request_id"))) | map({key: $n[.request_id], value: .data}) | from_entries' || true
}
ticks() { awk '{ sub(/.*\) /, ""); print $12 + $13 }' "/proc/$1/stat" 2>/dev/null || echo 0; }

BAIRD_WAS_ACTIVE=0
if systemctl --user is-active --quiet baird.service; then BAIRD_WAS_ACTIVE=1; systemctl --user stop baird.service; fi
trap '[[ -n "${PID:-}" ]] && kill "$PID" 2>/dev/null; [[ $BAIRD_WAS_ACTIVE -eq 1 ]] && systemctl --user start baird.service' EXIT

SOCK="/tmp/baird-x11-baseline.sock"
rm -f "$SOCK"
extra=()
[[ -n "$VIDEO_SYNC" ]] && extra+=(--video-sync="$VIDEO_SYNC")
read -r -a extra_words <<<"$EXTRA"
extra+=("${extra_words[@]}")
deint=(--deinterlace=yes)
[[ "$EXTRA" == *"--vf="* ]] && deint=(--deinterlace=no) # explicit deinterlace filter given
DISPLAY="${DISPLAY:-:0}" mpv --vo=gpu --gpu-api="$GPU_API" --hwdec=drm --fs "${deint[@]}" \
    --input-ipc-server="$SOCK" --no-osc --osd-level=0 --really-quiet "${extra[@]}" \
    --log-file="$HOME/x11-baseline-$GPU_API${VIDEO_SYNC:+-$VIDEO_SYNC}${LABEL:+-$LABEL}.mpv.log" "$url" >/dev/null 2>&1 &
PID=$!
sleep 12 # start + settle
a="$(query)"; t0="$(ticks "$PID")"; s0="$(date +%s.%N)"
sleep "$SECONDS_PER_RUN"
b="$(query)"; t1="$(ticks "$PID")"; s1="$(date +%s.%N)"
cpu="$(awk -v a="$t0" -v b="$t1" -v hz="$(getconf CLK_TCK)" -v s0="$s0" -v s1="$s1" 'BEGIN {printf "%d", (b - a) * 100 / (hz * (s1 - s0))}')"
[[ -n "$a" ]] || a='{}'
[[ -n "$b" ]] || b='{}'
d() { jq -rn --argjson a "$a" --argjson b "$b" --arg k "$1" '(($b[$k] // 0) - ($a[$k] // 0))'; }
echo "| X11 gpu-$GPU_API${VIDEO_SYNC:+ $VIDEO_SYNC}${EXTRA:+ $EXTRA} | $(jq -r '."current-vo"' <<<"$b") | $(jq -r '."hwdec-current"' <<<"$b") | avsync now $(jq -r '(.avsync // 0) * 1000 | floor' <<<"$b") ms | drops dec $(d decoder-frame-drop-count) / vo $(d frame-drop-count) / delayed $(d vo-delayed-frame-count) / mistimed $(d mistimed-frame-count) in ${SECONDS_PER_RUN}s | display sync $(jq -r '."display-sync-active" | tostring' <<<"$b") | mpv CPU $cpu% |"
