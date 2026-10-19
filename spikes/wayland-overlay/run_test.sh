#!/bin/bash
# Baird Wayland overlay spike — run ON THE PI, inside (or over SSH to) a labwc session.
#
# Question being answered: can mpv present video in its own Wayland window (so it gets
# real presentation timing, and ideally a hardware plane) while a transparent Avalonia
# window — standing in for Baird's UI — is composited on top?
#
# For each video output (dmabuf-wayland, gpu-next, gpu) it runs:
#   A. mpv alone                 (baseline for that output)
#   B. mpv + transparent overlay (HUD animating at ~50 Hz, opaque "menu" toggling every 10 s)
# and records mpv's A/V timing, drops, CPU of every process involved, DRM plane usage,
# the HDMI "Broadcast RGB" setting, and a screenshot. Results: ~/baird-wayland-spike/<time>/
#
# Usage:
#   ./run_test.sh --channel "BBC ONE HD"          # live TV via TvHeadend (uses ~/.baird/config.ini)
#   ./run_test.sh --source /path/to/hevc.mkv      # any file/URL mpv can play
#   (repeat --source/--channel to test several; default output dir can be set with --out)
# Options:
#   --seconds N        sampling time per phase (default 60)
#   --vo LIST          comma list of mpv video outputs (default dmabuf-wayland,gpu-next,gpu)
#                      dmabuf-wayland needs hardware-decoded frames (HEVC via --hwdec=drm);
#                      software-decoded H.264 is expected to fail there and need gpu/gpu-next.
#   --hwdec MODE       mpv hwdec (default drm: Pi 5 HEVC decoder; H.264 falls back to software)
#   --video-sync MODE  e.g. display-resample (default: mpv's own default, audio)
#   --extra "OPTS"     extra mpv options, e.g. "--profile=fast" (also added to the phase name)
#   --gpu-api API      opengl or vulkan for gpu/gpu-next (default auto = Vulkan; on the Pi 5
#                      under labwc 0.9 Vulkan swapchains fail continuously — use opengl)
#   --keep-baird       don't stop baird.service during the test
#   --calm-overlay     overlay repaints once a second (realistic HUD) instead of ~50 Hz
#
# Needs: labwc session, mpv, socat, jq. Optional: drm_info (planes, Broadcast RGB), grim.
# The OverlayTest binary is expected next to this script (see make_kit.sh).
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SECONDS_PER_PHASE=60
VOS="dmabuf-wayland,gpu-next,gpu"
HWDEC="drm"
VIDEO_SYNC=""   # empty = mpv default (audio)
GPU_API=""      # empty = mpv default (auto: Vulkan if available)
EXTRA=""        # extra mpv options, e.g. "--profile=fast"
KEEP_BAIRD=0
OUT_ROOT="$HOME/baird-wayland-spike"
OVERLAY_BIN="$SCRIPT_DIR/OverlayTest"
OVERLAY_ARGS=(--cycle)
SOURCES=()   # entries "label|url|live(0/1)"

die() { echo "ERROR: $*" >&2; exit 1; }
log() { echo "[spike] $(date +%H:%M:%S) $*" | tee -a "$OUT/run.log" >&2; }

# ------------------------------------------------------------------ TvHeadend ---
ini_get() { # key -> value from ~/.baird/config.ini (flat KEY=VALUE, last wins)
    local f="$HOME/.baird/config.ini"
    [[ -f "$f" ]] || return 1
    sed -nE "s/^[[:space:]]*$1[[:space:]]*=[[:space:]]*(.*)$/\1/p" "$f" | tail -n1 | tr -d '\r'
}

tvh_channel_url() { # channel name -> stream URL, built exactly like Baird's TvHeadendService
    local name="$1" base user pass uuid
    base="$(ini_get TVH_URL || echo http://localhost:9981)"
    base="${base%/}"
    user="$(ini_get TVH_USER)" || die "TVH_USER not in ~/.baird/config.ini"
    pass="$(ini_get TVH_PASS)" || die "TVH_PASS not in ~/.baird/config.ini"
    uuid="$(curl -fsS --digest -u "$user:$pass" "$base/api/channel/grid?limit=9999" |
        jq -r --arg n "$name" '.entries[] | select((.name | ascii_downcase) == ($n | ascii_downcase)) | .uuid' | head -n1)"
    [[ -n "$uuid" ]] || die "channel '$name' not found on TvHeadend ($base)"
    local enc_user enc_pass
    enc_user="$(jq -rn --arg s "$user" '$s|@uri')"
    enc_pass="$(jq -rn --arg s "$pass" '$s|@uri')"
    echo "$base/stream/channel/$uuid?auth=$enc_user:$enc_pass&profile=pass"
}

redact() { # hide TvHeadend credentials in anything we print or save
    local s="$1" pass
    pass="$(ini_get TVH_PASS 2>/dev/null || true)"
    if [[ -n "$pass" ]]; then s="${s//$pass/***}"; fi
    echo "$s"
}

# --------------------------------------------------------------------- args ---
while [[ $# -gt 0 ]]; do
    case "$1" in
    --source) SOURCES+=("$(basename "${2%%\?*}")|$2|0"); shift 2 ;;
    --channel) SOURCES+=("$2|tvh:$2|1"); shift 2 ;;
    --seconds) SECONDS_PER_PHASE="$2"; shift 2 ;;
    --vo) VOS="$2"; shift 2 ;;
    --hwdec) HWDEC="$2"; shift 2 ;;
    --video-sync) VIDEO_SYNC="$2"; shift 2 ;;
    --gpu-api) GPU_API="$2"; shift 2 ;;
    --extra) EXTRA="$2"; shift 2 ;;
    --keep-baird) KEEP_BAIRD=1; shift ;;
    --out) OUT_ROOT="$2"; shift 2 ;;
    --overlay) OVERLAY_BIN="$2"; shift 2 ;;
    --calm-overlay) OVERLAY_ARGS=(--cycle --calm); shift ;;
    -h | --help) sed -n '2,25p' "$0"; exit 0 ;;
    *) die "unknown argument: $1 (see --help)" ;;
    esac
done
[[ ${#SOURCES[@]} -gt 0 ]] || die "give at least one --channel or --source (see --help)"
for tool in mpv socat jq curl; do command -v "$tool" >/dev/null || die "missing tool: $tool (sudo apt install $tool)"; done
[[ -x "$OVERLAY_BIN" ]] || die "OverlayTest binary not found at $OVERLAY_BIN (build with make_kit.sh)"

OUT="$OUT_ROOT/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"
REPORT="$OUT/report.md"
CLK_TCK="$(getconf CLK_TCK)"

# ------------------------------------------------- find the labwc session env ---
# Works both from a terminal inside the session and over SSH: borrow the environment of a
# process running inside the session.
discover_session() {
    if [[ -n "${WAYLAND_DISPLAY:-}" && -n "${DISPLAY:-}" ]]; then return 0; fi
    local pid env
    for pid in $(pgrep -u "$(id -u)" -x labwc) $(pgrep -u "$(id -u)"); do
        env="$(tr '\0' '\n' 2>/dev/null <"/proc/$pid/environ" || true)"
        if grep -q '^WAYLAND_DISPLAY=' <<<"$env" && grep -q '^DISPLAY=' <<<"$env"; then
            WAYLAND_DISPLAY="$(sed -n 's/^WAYLAND_DISPLAY=//p' <<<"$env")"
            DISPLAY="$(sed -n 's/^DISPLAY=//p' <<<"$env")"
            XDG_RUNTIME_DIR="$(sed -n 's/^XDG_RUNTIME_DIR=//p' <<<"$env")"
            XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
            export WAYLAND_DISPLAY DISPLAY XDG_RUNTIME_DIR
            return 0
        fi
    done
    return 1
}

pgrep -u "$(id -u)" -x labwc >/dev/null ||
    die "labwc is not running for $(whoami). Switch the Pi to labwc first: sudo raspi-config nonint do_wayland W3 && sudo reboot (revert later with W1)."
discover_session || die "couldn't find WAYLAND_DISPLAY/DISPLAY of the labwc session (start a terminal in the session once so XWayland is running, then retry)"

# -------------------------------------------------------- window rules (labwc) ---
RC_DIR="$HOME/.config/labwc"
RC="$RC_DIR/rc.xml"
RC_BACKUP="$OUT/rc.xml.before-spike"
RC_EXISTED=0
install_window_rules() {
    mkdir -p "$RC_DIR"
    if [[ -f "$RC" ]]; then
        RC_EXISTED=1
        cp "$RC" "$RC_BACKUP"
    elif [[ -f /etc/xdg/labwc/rc.xml ]]; then
        cp /etc/xdg/labwc/rc.xml "$RC" # a user rc.xml replaces the system one, so start from it
    else
        printf '<?xml version="1.0"?>\n<labwc_config>\n</labwc_config>\n' >"$RC"
    fi
    local rules
    rules="$(cat "$SCRIPT_DIR/labwc-window-rules.xml")"
    if grep -q '<windowRules>' "$RC"; then
        awk -v r="$rules" '{print} /<windowRules>/ && !done {print r; done=1}' "$RC" >"$RC.tmp"
    else
        awk -v r="$rules" '/<\/labwc_config>/ && !done {print "  <windowRules>"; print r; print "  </windowRules>"; done=1} {print}' "$RC" >"$RC.tmp"
    fi
    mv "$RC.tmp" "$RC"
    labwc --reconfigure 2>/dev/null || pkill -HUP -u "$(id -u)" -x labwc || true
    log "window rules installed in $RC (labwc reconfigured)"
}
restore_window_rules() {
    if [[ $RC_EXISTED -eq 1 ]]; then cp "$RC_BACKUP" "$RC"; else rm -f "$RC"; fi
    labwc --reconfigure 2>/dev/null || pkill -HUP -u "$(id -u)" -x labwc || true
}

# --------------------------------------------------------------- baird.service ---
BAIRD_WAS_ACTIVE=0
cleanup() {
    set +e
    [[ -n "${OVERLAY_PID:-}" ]] && kill "$OVERLAY_PID" 2>/dev/null
    [[ -n "${MPV_PID:-}" ]] && kill "$MPV_PID" 2>/dev/null
    restore_window_rules
    if [[ $BAIRD_WAS_ACTIVE -eq 1 ]]; then
        systemctl --user start baird.service && log "baird.service restarted"
    fi
    log "done. Report: $REPORT"
}
trap cleanup EXIT

if [[ $KEEP_BAIRD -eq 0 ]] && systemctl --user is-active --quiet baird.service 2>/dev/null; then
    BAIRD_WAS_ACTIVE=1
    systemctl --user stop baird.service
    log "stopped baird.service for the duration of the test"
fi
install_window_rules

# ---------------------------------------------------------------- preflight ---
broadcast_rgb() {
    if command -v drm_info >/dev/null; then
        drm_info 2>/dev/null | grep -A1 '"Broadcast RGB"' | tr -s ' ' | tr '\n' ' ' | sed 's/^ *//'
    elif command -v modetest >/dev/null; then
        modetest -M vc4 -c 2>/dev/null | grep -A3 'Broadcast RGB' | grep -E 'value' | tr -s ' '
    else
        echo "unknown (install drm-info: sudo apt install drm-info)"
    fi
}

{
    echo "# Baird Wayland overlay spike — $(date '+%Y-%m-%d %H:%M')"
    echo
    echo "## Environment"
    echo
    echo "- Host: $(hostname), $(grep PRETTY_NAME /etc/os-release | cut -d= -f2 | tr -d '"'), kernel $(uname -r)"
    echo "- Compositor: labwc $(labwc --version 2>/dev/null | head -n1 || echo '?') (WAYLAND_DISPLAY=$WAYLAND_DISPLAY, XWayland DISPLAY=$DISPLAY)"
    echo "- mpv: $(mpv --version | head -n1)"
    echo "- mpv video outputs available: $(mpv --vo=help 2>/dev/null | awk 'NR>1{print $1}' | grep -E 'dmabuf-wayland|gpu-next|^gpu$' | tr '\n' ' ')"
    echo "- mpv hwdec 'drm' available: $(mpv --hwdec=help 2>/dev/null | grep -qw drm && echo yes || echo NO)"
    echo "- Outputs: $(command -v wlr-randr >/dev/null && wlr-randr 2>/dev/null | grep -E 'current|^[A-Z]' | tr -s ' ' | tr '\n' ';' || echo 'wlr-randr not installed')"
    echo "- HDMI Broadcast RGB: $(broadcast_rgb)"
    echo "  (want limited 16:235 — under X11 this was pinned in xorg.conf; check the picture isn't washed out)"
    echo "- Throttling before test: $(vcgencmd get_throttled 2>/dev/null || echo n/a), temp $(vcgencmd measure_temp 2>/dev/null || echo n/a)"
    echo
} >"$REPORT"

# ------------------------------------------------------------- measurement ---
ticks_of() { # pid -> utime+stime ticks (0 if gone)
    awk '{ sub(/.*\) /, ""); print $12 + $13 }' "/proc/$1/stat" 2>/dev/null || echo 0
}

MPV_PROPS=(avsync total-avsync-change frame-drop-count decoder-frame-drop-count mistimed-frame-count
    vo-delayed-frame-count display-sync-active estimated-display-fps vsync-jitter current-vo
    hwdec-current video-params/pixelformat estimated-vf-fps playback-time)

mpv_query() { # sock -> one JSON object {prop: value}
    local sock="$1" i req=""
    for i in "${!MPV_PROPS[@]}"; do
        req+="{\"command\":[\"get_property\",\"${MPV_PROPS[$i]}\"],\"request_id\":$i}"$'\n'
    done
    printf '%s' "$req" | socat -t 1 - "UNIX-CONNECT:$sock" 2>/dev/null |
        jq -s --argjson names "$(printf '%s\n' "${MPV_PROPS[@]}" | jq -R . | jq -s .)" \
            'map(select(has("request_id"))) | map({key: $names[.request_id], value: .data}) | from_entries'
}

planes_snapshot() {
    command -v drm_info >/dev/null || { echo "drm_info not installed"; return; }
    # Planes currently scanning out a framebuffer, with their pixel format (DRM fourcc, e.g.
    # XR24/AR24 = RGB). A YUV format (NV12, YU12, P030...) on its own plane = video is on a
    # hardware plane, not composited by labwc.
    drm_info -j 2>/dev/null | jq -r '
        [.. | objects | select(has("planes")) | .planes[]? ] | unique_by(.id)[]
        | select(.properties.FB_ID.value != 0 and .properties.FB_ID.value != null)
        | (.fb.format // 0) as $f
        | ([$f % 256, ($f / 256 | floor) % 256, ($f / 65536 | floor) % 256, ($f / 16777216 | floor) % 256] | implode) as $fourcc
        | "plane \(.id): fb=\(.properties.FB_ID.value) format=\($fourcc) \(.properties.SRC_W.value // 0 | ./65536 | floor)x\(.properties.SRC_H.value // 0 | ./65536 | floor) zpos=\(.properties.zpos.value // "?")"' 2>/dev/null ||
        echo "couldn't parse drm_info"
}

run_phase() { # label url live vo overlay(0/1)
    local label="$1" url="$2" live="$3" vo="$4" with_overlay="$5"
    local tag
    tag="$(printf '%s' "${label}_${vo}${GPU_API:+-$GPU_API}${VIDEO_SYNC:+-$VIDEO_SYNC}${EXTRA:+-$EXTRA}$([[ "${OVERLAY_ARGS[*]}" == *--calm* && $with_overlay -eq 1 ]] && echo -calm)_$([[ $with_overlay -eq 1 ]] && echo overlay || echo alone)" | tr -c 'A-Za-z0-9_.-' '_')"
    local sock="$XDG_RUNTIME_DIR/baird-spike-mpv.sock" csv="$OUT/$tag.csv"
    rm -f "$sock"
    log "phase $tag: starting mpv (--vo=$vo --hwdec=$HWDEC${VIDEO_SYNC:+ --video-sync=$VIDEO_SYNC}${GPU_API:+ --gpu-api=$GPU_API}${EXTRA:+ $EXTRA})"

    local extra=()
    if [[ $live -eq 1 ]]; then extra+=(--deinterlace=yes); else extra+=(--loop-file=inf); fi
    [[ -n "$VIDEO_SYNC" ]] && extra+=(--video-sync="$VIDEO_SYNC")
    [[ -n "$GPU_API" && "$vo" != dmabuf-wayland ]] && extra+=(--gpu-api="$GPU_API")
    local extra_words=()
    read -r -a extra_words <<<"$EXTRA"
    extra+=("${extra_words[@]}")
    mpv --vo="$vo" --hwdec="$HWDEC" --fs --wayland-app-id=baird-video --input-ipc-server="$sock" \
        --no-osc --osd-level=0 --no-input-default-bindings --really-quiet "${extra[@]}" \
        --log-file="$OUT/$tag.mpv.log" "$url" >/dev/null 2>&1 &
    MPV_PID=$!

    local waited=0 started=0
    while ((waited < 25)); do
        sleep 1
        waited=$((waited + 1))
        kill -0 "$MPV_PID" 2>/dev/null || break
        # Playing = time advancing AND a video output up (audio alone isn't a pass).
        if [[ -S "$sock" ]] && mpv_query "$sock" | jq -e '."playback-time" != null and ."current-vo" != null' >/dev/null; then
            started=1
            break
        fi
    done
    if [[ $started -eq 0 ]]; then
        log "phase $tag: no video output came up (see $tag.mpv.log)"
        echo "| $tag | – | **FAILED: no video** | $(grep -E '\]\[e\]' "$OUT/$tag.mpv.log" 2>/dev/null | sed -E 's/^\[ *[0-9.]+\]\[e\]//' | sort -u | head -n3 | tr '\n' ';' | tr '|' '/' | cut -c1-220) ||||||||" >>"$OUT/phases.md"
        kill "$MPV_PID" 2>/dev/null || true
        wait "$MPV_PID" 2>/dev/null || true
        MPV_PID=""
        return 0
    fi

    local overlay_log="$OUT/$tag.overlay.log"
    OVERLAY_PID=""
    if [[ $with_overlay -eq 1 ]]; then
        "$OVERLAY_BIN" "${OVERLAY_ARGS[@]}" >"$overlay_log" 2>&1 &
        OVERLAY_PID=$!
        waited=0
        until grep -q ' ready' "$overlay_log" 2>/dev/null || ((waited >= 30)); do sleep 1; waited=$((waited + 1)); done
        grep -q ' ready' "$overlay_log" || log "phase $tag: overlay didn't report ready (see $tag.overlay.log)"
    fi
    sleep 3 # settle

    local labwc_pid xw_pid
    labwc_pid="$(pgrep -u "$(id -u)" -x labwc | head -n1)"
    xw_pid="$(pgrep -u "$(id -u)" -x Xwayland | head -n1 || true)"
    local t0_mpv t0_ov t0_lab t0_xw t_start
    t0_mpv="$(ticks_of "$MPV_PID")"
    t0_ov="$( [[ -n "$OVERLAY_PID" ]] && ticks_of "$OVERLAY_PID" || echo 0)"
    t0_lab="$(ticks_of "$labwc_pid")"
    t0_xw="$( [[ -n "$xw_pid" ]] && ticks_of "$xw_pid" || echo 0)"
    t_start="$(date +%s.%N)"

    local last_mpv="$t0_mpv" last_ov="$t0_ov" last_lab="$t0_lab" last_xw="$t0_xw" v
    echo "t,${MPV_PROPS[*]}" | tr ' ' ',' >"$csv"
    local first="" last="" s sample motion="?"
    for ((s = 1; s <= SECONDS_PER_PHASE; s++)); do
        sleep 1
        kill -0 "$MPV_PID" 2>/dev/null || { log "phase $tag: mpv exited early"; break; }
        # mpv occasionally refuses an IPC connection; skip that second rather than abort.
        sample="$(mpv_query "$sock" || true)"
        if [[ -z "$sample" ]] || ! jq -e 'type == "object"' >/dev/null 2>&1 <<<"$sample"; then continue; fi
        last="$sample"
        v="$(ticks_of "$MPV_PID")"; [[ "$v" != 0 ]] && last_mpv="$v"
        if [[ -n "$OVERLAY_PID" ]]; then v="$(ticks_of "$OVERLAY_PID")"; [[ "$v" != 0 ]] && last_ov="$v"; fi
        v="$(ticks_of "$labwc_pid")"; [[ "$v" != 0 ]] && last_lab="$v"
        if [[ -n "$xw_pid" ]]; then v="$(ticks_of "$xw_pid")"; [[ "$v" != 0 ]] && last_xw="$v"; fi
        [[ -z "$first" ]] && first="$last"
        jq -r --argjson names "$(printf '%s\n' "${MPV_PROPS[@]}" | jq -R . | jq -s .)" --arg t "$s" \
            '[$t] + [ $names[] as $n | (.[$n] | if . == null then "" else tostring end) ] | @csv' <<<"$last" >>"$csv"
        if ((s == SECONDS_PER_PHASE / 2)); then
            motion="$("$SCRIPT_DIR/motion_check.sh" wayland 10 2>/dev/null || echo "?")"
            planes_snapshot >"$OUT/$tag.planes.txt"
            command -v grim >/dev/null && grim "$OUT/$tag.png" 2>/dev/null || true
        fi
    done

    local elapsed cpu_mpv cpu_ov cpu_lab cpu_xw
    elapsed="$(awk -v a="$(date +%s.%N)" -v b="$t_start" 'BEGIN {print a - b}')"
    pct() { awk -v now="$1" -v before="$2" -v hz="$CLK_TCK" -v secs="$elapsed" 'BEGIN {printf "%d", (now - before) * 100 / (hz * secs)}'; }
    cpu_mpv="$(pct "$last_mpv" "$t0_mpv")"
    cpu_ov="$( [[ -n "$OVERLAY_PID" ]] && pct "$last_ov" "$t0_ov" || echo -)"
    cpu_lab="$(pct "$last_lab" "$t0_lab")"
    cpu_xw="$( [[ -n "$xw_pid" ]] && pct "$last_xw" "$t0_xw" || echo -)"

    # Summaries from the CSV (columns follow MPV_PROPS, offset by the t column)
    local avs_mean avs_max
    avs_mean="$(awk -F, 'NR>1 && $2!="\"\"" {gsub(/"/,"",$2); if ($2!="") {s+=$2; n++}} END {printf "%.1f", n? s/n*1000 : 0}' "$csv")"
    avs_max="$(awk -F, 'NR>1 {gsub(/"/,"",$2); x=$2+0; v=x<0?-x:x; if (v>m) m=v} END {printf "%.1f", m*1000}' "$csv")"
    [[ -n "$first" ]] || first='{}'
    [[ -n "$last" ]] || last='{}'
    delta() { jq -rn --argjson a "$first" --argjson b "$last" --arg k "$1" '(($b[$k] // 0) - ($a[$k] // 0))'; }
    local planes transp focus_lost vo_errors
    vo_errors="$(grep -c '\]\[e\]\[vo' "$OUT/$tag.mpv.log" 2>/dev/null || true)"
    planes="n/a"
    if [[ -s "$OUT/$tag.planes.txt" ]]; then
        planes="$(grep -c '^plane' "$OUT/$tag.planes.txt" || true) active; $(grep -oE 'format=[^ ]+' "$OUT/$tag.planes.txt" | sort | uniq -c | tr -s ' ' | tr '\n' ' ' || true)"
    fi
    transp="-"
    focus_lost="-"
    if [[ $with_overlay -eq 1 ]]; then
        transp="$(sed -n 's/.*actual-transparency=//p' "$overlay_log" 2>/dev/null | head -n1)"
        focus_lost="$(grep -c 'DEACTIVATED' "$overlay_log" 2>/dev/null || true)"
    fi

    local motion_cell="$motion"
    [[ "${motion%%/*}" =~ ^[0-9]+$ ]] && ((${motion%%/*} <= 2)) && motion_cell="**FROZEN $motion**"
    echo "| $tag | $motion_cell | $(jq -r '."current-vo" // "?"' <<<"$last") | $(jq -r '."hwdec-current" // "?"' <<<"$last") $(jq -r '."video-params/pixelformat" // ""' <<<"$last") | $avs_mean / $avs_max | $(delta decoder-frame-drop-count) / $(delta frame-drop-count) / $(delta vo-delayed-frame-count) / $(delta mistimed-frame-count) | $(jq -r '."display-sync-active" | if . == null then "?" else tostring end' <<<"$last"), vsync jitter $(jq -r '."vsync-jitter" // "?"' <<<"$last") | mpv $cpu_mpv% · overlay $cpu_ov% · labwc $cpu_lab% · Xwayland $cpu_xw% | $planes | $transp | $focus_lost | $vo_errors |" >>"$OUT/phases.md"

    if [[ -n "$OVERLAY_PID" ]]; then kill "$OVERLAY_PID" 2>/dev/null || true; wait "$OVERLAY_PID" 2>/dev/null || true; fi
    kill "$MPV_PID" 2>/dev/null || true
    wait "$MPV_PID" 2>/dev/null || true
    OVERLAY_PID=""
    MPV_PID=""
    sleep 2
}

# -------------------------------------------------------------------- run ---
echo "| phase | picture moving (distinct/10 frames) | vo | hwdec / pixfmt | avsync mean / max-abs (ms) | drops dec / vo / delayed / mistimed | display sync | CPU (% of one core) | DRM planes (mid-run) | overlay transparency | overlay focus lost | vo errors logged |" >"$OUT/phases.md"
echo "|---|---|---|---|---|---|---|---|---|---|---|---|" >>"$OUT/phases.md"

IFS=',' read -r -a VO_LIST <<<"$VOS"
for entry in "${SOURCES[@]}"; do
    IFS='|' read -r label src live <<<"$entry"
    url="$src"
    [[ "$src" == tvh:* ]] && url="$(tvh_channel_url "${src#tvh:}")"
    log "source '$label': $(redact "$url")"
    for vo in "${VO_LIST[@]}"; do
        run_phase "$label" "$url" "$live" "$vo" 0
        run_phase "$label" "$url" "$live" "$vo" 1
    done
done

{
    echo "## Results"
    echo
    cat "$OUT/phases.md"
    echo
    echo "- Throttling after test: $(vcgencmd get_throttled 2>/dev/null || echo n/a), temp $(vcgencmd measure_temp 2>/dev/null || echo n/a)"
    echo
    echo "## How to read this"
    echo
    echo "- **picture moving** is measured from screenshots, not mpv: ~10/10 = live motion, ≤2/10 = FROZEN on screen"
    echo "  (mpv's counters can look fine while nothing reaches the display)."
    echo "- **overlay transparency** must be \`Transparent\`; anything else means XWayland/Avalonia gave us an opaque window (video hidden)."
    echo "- **overlay focus lost** should be 0: mpv must never take keyboard focus from the UI."
    echo "- **DRM planes**: a YUV format (NV12/SAND128/YU12...) on its own plane means the video is scanned out directly"
    echo "  (zero-copy, exact timing). Only RGB formats means labwc is compositing the video on the GPU (still fine, costs GPU)."
    echo "- Compare **CPU** and **avsync/drops** between 'alone' and 'overlay' phases: the overlay should cost little and not disturb timing."
    echo "- Per-second data: \`*.csv\`; mpv logs: \`*.mpv.log\`; overlay logs: \`*.overlay.log\`; screenshots: \`*.png\`."
    echo
    echo "## Things only a human can check (watch the TV during the overlay phases)"
    echo
    echo "1. Video plays **underneath** the HUD bar and is visible through it (translucent black bar, moving orange dot)."
    echo "2. The opaque **menu panel** appearing every 10 s fully hides the video behind it."
    echo "3. Picture is **not washed out** (black is black) — HDMI colour range under Wayland."
    echo "4. No judder, tearing or flicker when the menu panel toggles."
    echo "5. Lip sync feels right without any audio-delay adjustment (compare with Baird today)."
} >>"$REPORT"
