#!/bin/bash
# Baird auto-updater. Run hourly by baird-updater.timer (systemd user unit).
#
#   1. brew update; exit if the installed version is already the latest.
#   2. Skip a version that was previously rolled back (until a newer one ships).
#   3. Defer if the TV looks busy (Baird's own idle/active state in ~/.baird/state; for an older Baird, audio stream / high CPU), unless inside the
#      upgrade window (03:00-06:00 local) or FORCE=1.
#   4. brew upgrade, keeping the previous keg (HOMEBREW_NO_INSTALL_CLEANUP).
#   5. Run the new install_service.sh, then watch baird.service for
#      HEALTH_SECONDS. Any restart, PID change or non-active state => roll back
#      to the previous keg, record the bad version and `brew pin` the formula.
#
# Env overrides:
#   FORCE=1                  ignore the playback heuristic and upgrade window
#   HEALTH_SECONDS=90        how long baird must stay up after an upgrade
#   IDLE_CPU_PCT=30          baird CPU% (of one core) above which we assume playback
#   UPGRADE_WINDOW_START=3   hour (local, inclusive) upgrades are always allowed from
#   UPGRADE_WINDOW_END=6     hour (local, exclusive) the window ends
#
# To retry a rolled-back version by hand:
#   rm ~/.local/state/baird-updater/bad-version && brew unpin uatec/tools/baird
#   FORCE=1 /home/linuxbrew/.linuxbrew/opt/baird/libexec/update_baird.sh
set -euo pipefail

export PATH="/home/linuxbrew/.linuxbrew/bin:$PATH"
# Keep the previous keg on disk so we can roll back to it; we clean up
# ourselves once the new version has proven healthy.
export HOMEBREW_NO_INSTALL_CLEANUP=1
export HOMEBREW_NO_AUTO_UPDATE=1 # we run `brew update` explicitly
export HOMEBREW_NO_ENV_HINTS=1

FORMULA="uatec/tools/baird"
SERVICE="baird.service"
FORCE="${FORCE:-0}"
HEALTH_SECONDS="${HEALTH_SECONDS:-90}"
HEALTH_INTERVAL=5
IDLE_CPU_PCT="${IDLE_CPU_PCT:-30}"
UPGRADE_WINDOW_START="${UPGRADE_WINDOW_START:-3}"
UPGRADE_WINDOW_END="${UPGRADE_WINDOW_END:-6}"
STATE_DIR="${XDG_STATE_HOME:-$HOME/.local/state}/baird-updater"
BAD_VERSION_FILE="$STATE_DIR/bad-version"
# The brew `baird` wrapper gunzips libexec/Baird.gz here and only refreshes it
# when the .gz is newer (-nt) than the cached copy. gzip keeps the CI build
# mtime, so after an upgrade *or* a rollback the stale copy could be reused.
# Delete it whenever we switch versions.
BINARY_CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/baird/Baird"

# ---------------------------------------------------------------- logging ---
# Under systemd, stdout goes to the journal; a "<N>" prefix sets the syslog
# priority (SyslogLevelPrefix= defaults to yes), so rollbacks show up in red
# in `journalctl --user -u baird-updater -p warning`.
_log() {
    local prio="$1"
    shift
    if [[ -n "${JOURNAL_STREAM:-}" ]]; then
        printf '<%s>%s\n' "$prio" "$*"
    else
        printf '%s\n' "$*"
    fi
}
info() { _log 6 "$*"; }
warn() { _log 4 "WARNING: $*"; }
err() { _log 3 "ERROR: $*"; }
crit() { _log 2 "CRITICAL: $*"; }

# ---------------------------------------------------------------- helpers ---
brew_prefix() { brew --prefix; }

# Version of the currently linked keg, from the opt symlink
# (/home/linuxbrew/.linuxbrew/opt/baird -> ../Cellar/baird/<version>).
installed_version() {
    local opt
    opt="$(brew_prefix)/opt/baird"
    [[ -e "$opt" ]] || return 1
    basename "$(readlink -f "$opt")"
}

# Latest version in the tap. The first string-valued "stable" key in
# `brew info --json=v2` is versions.stable (urls.stable is an object).
latest_version() {
    local json
    json="$(brew info --json=v2 "$FORMULA")" || return 1
    # sed (not head) so nothing upstream gets SIGPIPE under pipefail.
    grep -oE '"stable"[[:space:]]*:[[:space:]]*"[^"]+"' <<<"$json" |
        sed -nE '1s/.*"([^"]+)"$/\1/p'
}

svc_prop() { systemctl --user show -p "$1" --value "$SERVICE"; }
svc_state() { systemctl --user is-active "$SERVICE" 2>/dev/null || true; }

journal_tail() {
    # baird.service logs to the system journal (SyslogIdentifier=baird), not the user journal
    journalctl -t baird -n 40 --no-pager 2>/dev/null || true
}

in_upgrade_window() {
    local hour
    hour="$(date +%-H)"
    ((hour >= UPGRADE_WINDOW_START && hour < UPGRADE_WINDOW_END))
}

# --- playback heuristic (no IPC to the app, so infer from the outside) ---

# 1. A PulseAudio/PipeWire stream owned by the baird process that isn't
#    corked (paused). pipewire-pulse also lists native PipeWire streams.
baird_audio_stream_active() {
    local pid="$1"
    command -v pactl >/dev/null 2>&1 || return 1
    pactl info >/dev/null 2>&1 || return 1
    pactl list sink-inputs 2>/dev/null | awk -v pid="$pid" '
        /^Sink Input #/ { if (match_pid && !corked) found = 1; match_pid = 0; corked = 0 }
        /Corked: yes/ { corked = 1 }
        $0 ~ "application.process.id = \"" pid "\"" { match_pid = 1 }
        END { if (match_pid && !corked) found = 1; exit(found ? 0 : 1) }
    '
}

# 2. Any ALSA playback substream currently RUNNING (covers mpv using ALSA
#    directly, or a sound server that is actively feeding the HDMI sink).
alsa_playback_running() {
    local f
    for f in /proc/asound/card*/pcm*p/sub*/status; do
        [[ -r "$f" ]] || continue
        if grep -q '^state: RUNNING' "$f" 2>/dev/null; then
            return 0
        fi
    done
    return 1
}

# 3. CPU fallback: software video decode on the Pi 5 (no H.264 hw decode)
#    keeps baird well above idle. Prints the % of one core over 5s.
baird_cpu_pct() {
    local pid="$1" ticks t1 t2 secs=5
    ticks="$(getconf CLK_TCK)"
    # Fields after the "(comm)" field: utime is 14th, stime 15th overall.
    t1="$(awk '{ sub(/.*\) /, ""); print $12 + $13 }' "/proc/$pid/stat" 2>/dev/null)" || return 1
    sleep "$secs"
    t2="$(awk '{ sub(/.*\) /, ""); print $12 + $13 }' "/proc/$pid/stat" 2>/dev/null)" || return 1
    echo $(((t2 - t1) * 100 / (ticks * secs)))
}

# Baird says whether anyone could be watching: ~/.baird/state holds "idle" (screensaver up, or the TV in standby) or
# "active". Prints that word, or nothing when there is no file (a Baird from before it wrote one).
baird_reported_state() {
    local f="${BAIRD_STATE_FILE:-$HOME/.baird/state}" word=""
    [[ -r "$f" ]] && read -r word <"$f"
    [[ "$word" == "idle" || "$word" == "active" ]] && echo "$word"
    return 0
}

# Returns 0 (busy) if something looks like it is playing; logs why.
tv_is_busy() {
    local pid cpu state
    pid="$(svc_prop MainPID)"
    if [[ -z "$pid" || "$pid" == "0" ]]; then
        return 1 # app not running: nothing to interrupt
    fi
    # Baird knows best: its screensaver (10 idle minutes in a menu; 3 hours with a stream, after a "Still watching?") is "idle".
    state="$(baird_reported_state)"
    if [[ "$state" == "idle" ]]; then
        info "Playback check: Baird reports idle (screensaver up or TV in standby)."
        return 1
    elif [[ "$state" == "active" ]]; then
        info "Playback check: Baird reports it is in use."
        return 0
    fi
    # No state file: an older Baird. Infer from the outside.
    if baird_audio_stream_active "$pid"; then
        info "Playback check: baird (pid $pid) has an active audio stream."
        return 0
    fi
    if alsa_playback_running; then
        info "Playback check: an ALSA playback device is running."
        return 0
    fi
    if cpu="$(baird_cpu_pct "$pid")"; then
        if ((cpu >= IDLE_CPU_PCT)); then
            info "Playback check: baird CPU ${cpu}% >= ${IDLE_CPU_PCT}% (likely decoding video)."
            return 0
        fi
        info "Playback check: idle (no audio stream, CPU ${cpu}%)."
    fi
    return 1
}

# Watches baird.service for HEALTH_SECONDS. Healthy = active the whole time
# with the same MainPID and no automatic restarts.
service_is_stable() {
    local base_restarts base_pid state restarts pid elapsed=0
    sleep 2
    state="$(svc_state)"
    if [[ "$state" != "active" ]]; then
        err "$SERVICE is '$state' immediately after restart."
        return 1
    fi
    base_restarts="$(svc_prop NRestarts)"
    base_pid="$(svc_prop MainPID)"
    while ((elapsed < HEALTH_SECONDS)); do
        sleep "$HEALTH_INTERVAL"
        elapsed=$((elapsed + HEALTH_INTERVAL))
        state="$(svc_state)"
        restarts="$(svc_prop NRestarts)"
        pid="$(svc_prop MainPID)"
        if [[ "$state" != "active" || "$restarts" != "$base_restarts" || "$pid" != "$base_pid" ]]; then
            err "$SERVICE unstable after ${elapsed}s: state=$state NRestarts=$base_restarts->$restarts MainPID=$base_pid->$pid"
            return 1
        fi
    done
    info "$SERVICE stable for ${HEALTH_SECONDS}s (pid $base_pid)."
    return 0
}

rollback() {
    local prev="$1" bad="$2" rack="$3" reason="$4"
    crit "ROLLING BACK baird $bad -> $prev: $reason"
    crit "Last $SERVICE log lines follow:"
    journal_tail | while IFS= read -r line; do _log 3 "  | $line"; done

    if [[ ! -d "$rack/$prev" ]]; then
        crit "Cannot roll back: previous keg $rack/$prev is gone. baird $bad left installed."
        exit 1
    fi

    # Only one keg left => `brew link` relinks it (and repoints opt/baird).
    brew unlink "$FORMULA" || true
    rm -rf "${rack:?}/${bad:?}"
    if ! brew link --overwrite "$FORMULA"; then
        crit "brew link of $prev failed. Fix by hand: brew link --overwrite $FORMULA"
        exit 1
    fi
    rm -f "$BINARY_CACHE"

    local now
    now="$(installed_version || echo none)"
    if [[ "$now" != "$prev" ]]; then
        crit "Rollback relink failed: linked version is '$now', expected '$prev'."
        exit 1
    fi

    # Don't retry the bad version every poll. The state file lets the next
    # run auto-unpin once a *newer* version is published; the pin also stops a
    # manual `brew upgrade` from pulling the bad version back in.
    echo "$bad" >"$BAD_VERSION_FILE"
    brew pin "$FORMULA" || warn "brew pin failed"

    # Restore the previous version's unit files and restart the app.
    "$rack/$prev/libexec/install_service.sh" || crit "Previous install_service.sh failed."

    if service_is_stable; then
        crit "Rolled back to baird $prev; $bad is pinned out until a newer release."
    else
        crit "Rolled back to baird $prev but it is ALSO unstable. Manual attention needed."
    fi
    # Non-zero so the updater unit shows as failed (systemctl --user --failed).
    exit 1
}

# ------------------------------------------------------------------- main ---
mkdir -p "$STATE_DIR"
exec 9>"$STATE_DIR/lock"
if ! flock -n 9; then
    info "Another update run is in progress; exiting."
    exit 0
fi

# StartLimitBurst in baird.service turns a crash loop into "failed", which
# would otherwise leave the TV blank until someone intervenes. Give it another
# go each run (at most ~10 attempts/hour before it trips the limit again).
if systemctl --user is-failed --quiet "$SERVICE"; then
    warn "$SERVICE is in failed state; resetting and starting it."
    journal_tail | while IFS= read -r line; do _log 4 "  | $line"; done
    systemctl --user reset-failed "$SERVICE" || true
    systemctl --user start "$SERVICE" || err "Failed to start $SERVICE."
fi

info "Checking for updates..."
brew update --quiet

current="$(installed_version)" || {
    err "baird is not installed via brew (no $(brew_prefix)/opt/baird)."
    exit 1
}
latest="$(latest_version || true)"
if [[ -z "$latest" ]]; then
    err "Could not determine latest version of $FORMULA."
    exit 1
fi

# Keg dirs carry a formula revision suffix (0.0.201_1); the tap version doesn't.
if [[ "${current%_*}" == "$latest" ]]; then
    info "Baird is already up to date ($current)."
    # yt-dlp breaks (YouTube changes) between Baird releases; keep it current regardless.
    "$(dirname "${BASH_SOURCE[0]}")/update_ytdlp.sh" | while IFS= read -r line; do info "$line"; done
    exit 0
fi

if [[ -f "$BAD_VERSION_FILE" ]]; then
    bad_version="$(<"$BAD_VERSION_FILE")"
    if [[ "$latest" == "$bad_version" ]]; then
        warn "Latest baird $latest was rolled back previously; staying on $current until a newer release."
        exit 0
    fi
    info "Newer release $latest supersedes rolled-back $bad_version; unpinning."
    brew unpin "$FORMULA" || true
    rm -f "$BAD_VERSION_FILE"
fi

info "Update available: baird $current -> $latest."

if [[ "$FORCE" == "1" ]]; then
    info "FORCE=1: skipping playback check."
elif in_upgrade_window; then
    info "Inside upgrade window (${UPGRADE_WINDOW_START}:00-${UPGRADE_WINDOW_END}:00): skipping playback check."
elif tv_is_busy; then
    info "Deferring upgrade to $latest: TV appears to be in use. Will retry next run (FORCE=1 to override)."
    exit 0
fi

rack="$(brew --cellar "$FORMULA")"
prev="$current"
was_active="$(svc_state)"
if [[ ! -d "$rack/$prev" ]]; then
    warn "Previous keg $rack/$prev not found; rollback will not be possible."
fi

info "Upgrading baird $prev -> $latest..."
if ! brew upgrade "$FORMULA"; then
    err "brew upgrade failed; staying on $(installed_version || echo unknown)."
    exit 1
fi

new="$(installed_version)"
if [[ "$new" == "$prev" ]]; then
    err "brew upgrade succeeded but linked version is still $prev."
    exit 1
fi
info "Upgraded to baird $new; installing services..."
rm -f "$BINARY_CACHE"

if ! "$(brew_prefix)/opt/baird/libexec/install_service.sh"; then
    rollback "$prev" "$new" "$rack" "install_service.sh from $new failed"
fi

if [[ "$was_active" != "active" ]]; then
    warn "$SERVICE was '$was_active' before the upgrade (no graphical session?); skipping health check and rollback."
    exit 0
fi

info "Watching $SERVICE for ${HEALTH_SECONDS}s..."
if ! service_is_stable; then
    rollback "$prev" "$new" "$rack" "$SERVICE did not stay up after upgrade"
fi

info "baird $new is healthy; removing old kegs."
brew cleanup "$FORMULA" || warn "brew cleanup failed (harmless)"
info "Update to $new complete."
