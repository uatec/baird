#!/bin/bash
# Quick on-device A/B of Baird's own video settings (X11 session), read from Baird's A/V Debug
# journal logging rather than a separate mpv. For each variant: set the [Baird] settings in
# ~/.baird/config.ini, restart baird.service, wait for auto-play, turn on A/V Debug journal
# logging via key presses (xdotool), then measure for WINDOW seconds: VO/decoder drop deltas,
# A/V offset, CPU, and on-screen motion (motion_check.sh). Restores the original config after.
#
# Usage: ./baird_ab.sh [window_seconds=20] [test-binary]
#   test-binary: optional Baird build to run instead of the installed one (placed in the brew
#   wrapper's cache; removed afterwards so the wrapper re-extracts the official build).
set -euo pipefail
cd "$(dirname "$0")"
WINDOW="${1:-20}"
CFG="$HOME/.baird/config.ini"
export DISPLAY=:0
# Under labwc, Baird is an XWayland client: xdotool still works through DISPLAY, but screenshots
# must come from the compositor (grim), since XWayland's root window isn't what's on screen.
MOTION_MODE=x11
if pgrep -u "$(id -u)" -x labwc >/dev/null; then
    export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
    export WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-wayland-0}"
    MOTION_MODE=wayland
fi
TEST_BIN="${2:-}"
CACHE_BIN="$HOME/.cache/baird/Baird"
cp "$CFG" /tmp/baird-config.before-ab
restore() {
    cp /tmp/baird-config.before-ab "$CFG"
    [[ -n "$TEST_BIN" ]] && rm -f "$CACHE_BIN" # wrapper re-extracts the installed release
    systemctl --user restart baird.service
}
trap restore EXIT
if [[ -n "$TEST_BIN" ]]; then
    systemctl --user stop baird.service
    install -m 755 "$TEST_BIN" "$CACHE_BIN"
fi

set_mpv_section() { # replace the [Mpv] section with the given KEY=VALUE lines (none = remove)
    awk 'BEGIN{skip=0} /^\[/{skip=($0=="[Mpv]")} !skip' "$CFG" >"$CFG.tmp" && mv "$CFG.tmp" "$CFG"
    if [[ $# -gt 0 ]]; then
        printf '\n[Mpv]\n' >>"$CFG"
        printf '%s\n' "$@" >>"$CFG"
    fi
}
key() { xdotool key --delay 150 "$@"; sleep 0.4; }

run_variant() { # label hq sharper [mpv KEY=VALUE...]
    # hq / sharper used to be Baird settings (removed 2026-10-25); they are now passed to mpv through [Mpv] only, which
    # restore() puts back, so an interrupted run can't leave them switched on.
    local label="$1"
    local opts=()
    [[ "$2" == true ]] && opts+=("scale=spline36" "cscale=spline36" "dscale=mitchell")
    [[ "$3" == true ]] && opts+=("deinterlace=no" "vf=bwdif=mode=send_field:deint=interlaced")
    set_mpv_section "${opts[@]}" "${@:4}"
    local since
    since="$(date '+%Y-%m-%d %H:%M:%S')"
    systemctl --user restart baird.service
    # Wait for the first stream to load (auto-play of the first channel).
    local t=0
    until journalctl -t baird --since "$since" --no-pager | grep -q 'FileLoaded event received'; do
        sleep 1; t=$((t + 1)); ((t < 60)) || { echo "| $label | auto-play did not start |"; return; }
    done
    sleep 3
    # First key may only wake CEC input lock (6 s fallback), then open menu → A/V Debug → logging.
    key Escape; sleep 7
    key Down; sleep 1
    for _ in 1 2 3 4 5 6; do key Right; done
    key Down Right Return
    key Escape
    sleep 2
    local start end
    start="$(date '+%Y-%m-%d %H:%M:%S')"
    sleep $((WINDOW / 2))
    local motion
    motion="$(./motion_check.sh "$MOTION_MODE" 10 2>/dev/null || echo '?')"
    sleep $((WINDOW - WINDOW / 2))
    end="$(date '+%Y-%m-%d %H:%M:%S')"
    local lines
    lines="$(journalctl -t baird --since "$start" --until "$end" --no-pager | grep -E '\[AvDebug\] state=' || true)"
    if [[ -z "$lines" ]]; then echo "| $label | no AvDebug lines (logging toggle missed?) | moving $motion |"; return; fi
    local first last
    first="$(head -n1 <<<"$lines")"
    last="$(tail -n1 <<<"$lines")"
    f() { grep -oE "$1=[^ ]*" <<<"$2" | head -n1 | cut -d= -f2-; }
    local d0 d1 v0 v1
    IFS=/ read -r d0 v0 <<<"$(f 'drops\(dec\/vo\)' "$first")"
    IFS=/ read -r d1 v1 <<<"$(f 'drops\(dec\/vo\)' "$last")"
    echo "| $label | moving $motion | file=$(f file "$last") | vo drops $(( ${v1:-0} - ${v0:-0} )) / dec drops $(( ${d1:-0} - ${d0:-0} )) in $(wc -l <<<"$lines") s | avsync $(f avsync "$last") | late renders(last s) $(f late "$last") | cb2render $(f cb2render "$last") | cpu $(f cpu "$last") |"
}

echo "| variant | picture | playing | drops | A/V | | | |"
if [[ -n "${VARIANTS:-}" && "$VARIANTS" == mpv ]]; then
    run_variant "baseline (HQ off, bwdif off)" false false
    run_variant "video-sync=audio" false false "video-sync=audio"
    run_variant "yadif to 25 fps" false false "deinterlace=no" "vf=yadif=mode=send_frame:deint=interlaced"
    run_variant "audio sync + yadif 25 fps" false false "video-sync=audio" "deinterlace=no" "vf=yadif=mode=send_frame:deint=interlaced"
elif [[ "${VARIANTS:-}" == defaults ]]; then
    run_variant "build defaults (HQ off, 25 fps)" false false
    run_variant "build defaults + 50 fps flag" false true
elif [[ "${VARIANTS:-}" == sharp25 ]]; then
    run_variant "HQ scaling + yadif 25 fps" true false "deinterlace=no" "vf=yadif=mode=send_frame:deint=interlaced"
    run_variant "bwdif 25 fps" false false "deinterlace=no" "vf=bwdif=mode=send_frame:deint=interlaced"
    run_variant "HQ scaling + bwdif 25 fps" true false "deinterlace=no" "vf=bwdif=mode=send_frame:deint=interlaced"
else
    run_variant "HQ scaling ON, bwdif ON (current)" true true
    run_variant "HQ scaling OFF, bwdif OFF" false false
fi
