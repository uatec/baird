#!/bin/bash
# Runs ON THE PI. Checks Baird's transparency-capable main window (BAIRD_TRANSPARENT_WINDOW):
#   1. With a Chromium behind Baird, F9 (test build only) makes the video area transparent: screenshots before/after.
#   2. Live TV cost: the same auto-played channel with the hint off and on, A/V Debug drops + CPU + on-screen motion.
# Always restores baird.service.
#   usage: pi_transparency_ab.sh [seconds-per-variant=120] [baseline]
#     baseline: skip both checks above; instead compare live TV for the main-branch build in ~/baird-baseline and a test
#     build in ~/baird-mine, with the Netflix browser on and off (NETFLIX_USER= turns Netflix off).
#     compare: main (~/baird-baseline) vs ~/baird-final, alternating twice, Netflix browser on.
# Output: ~/baird-test/transparency/*.png, results on stdout.
set -u
SECS=${1:-120}; MODE=${2:-}
export XDG_RUNTIME_DIR=/run/user/$(id -u) WAYLAND_DISPLAY=wayland-0 DISPLAY=:0
T=$HOME/baird-test; OUT=$T/transparency; mkdir -p $OUT; rm -f $OUT/*
restore() {
  systemctl --user stop baird-ab.service baird-probe.service 2>/dev/null; sleep 2
  systemctl --user reset-failed baird.service baird-ab.service 2>/dev/null
  systemctl --user start baird.service
  echo "restored baird.service: $(systemctl --user is-active baird.service)"
}
trap restore EXIT
systemctl --user stop baird.service; sleep 2

start_baird() { # transparent=true|false [build-dir] [extra systemd-run args...]; sets START
  local dir=${2:-$T}; local extra=("${@:3}")
  systemctl --user stop baird-ab.service 2>/dev/null; sleep 2
  systemctl --user reset-failed baird-ab.service 2>/dev/null
  START=$(date +"%Y-%m-%d %H:%M:%S")
  systemd-run --user --unit=baird-ab --collect --property=SyslogIdentifier=baird-ab \
    --setenv=DISPLAY=:0 --setenv=BAIRD_FULLSCREEN=True --setenv=XAUTHORITY=$HOME/.Xauthority \
    --setenv=WAYLAND_DISPLAY=wayland-0 --setenv=XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR --setenv=LD_LIBRARY_PATH=$dir \
    --setenv=BAIRD_TRANSPARENT_WINDOW=$1 --setenv=BAIRD_AVDEBUG_LOG=true "${extra[@]}" "$dir/Baird" >/dev/null
  for _ in $(seq 1 90); do
    journalctl -t baird-ab --since "$START" --no-pager | grep -q "FileLoaded event received" && break
    sleep 1
  done
  journalctl -t baird-ab --since "$START" --no-pager | grep -o "\[App\] Main window transparency.*" | head -1
}

measure() { # label
  sleep 10
  local m0 m1 motion lines
  m0=$(date +"%Y-%m-%d %H:%M:%S"); sleep $((SECS / 2))
  motion=$(bash $T/motion_check.sh wayland 10 2>/dev/null || echo '?')
  sleep $((SECS - SECS / 2)); m1=$(date +"%Y-%m-%d %H:%M:%S")
  lines=$(journalctl -t baird-ab --since "$m0" --until "$m1" --no-pager | grep "\[AvDebug\] state=")
  if [ -z "$lines" ]; then echo "| $1 | no AvDebug lines | motion $motion |"; return; fi
  local d0 d1 cpu n
  d0=$(head -1 <<<"$lines" | grep -o "drops(dec/vo)=[0-9]*/[0-9]*" | cut -d= -f2)
  d1=$(tail -1 <<<"$lines" | grep -o "drops(dec/vo)=[0-9]*/[0-9]*" | cut -d= -f2)
  cpu=$(grep -o " cpu=[0-9]*%" <<<"$lines" | tr -dc '0-9\n' | awk '{s+=$1; n++} END {printf "%.0f", s/n}')
  n=$(wc -l <<<"$lines")
  echo "| $1 | drops dec/vo start $d0 end $d1 over $n samples | avg cpu ${cpu}% | motion $motion |"
}

if [ "$MODE" = compare ]; then # main (~/baird-baseline) vs this build (~/baird-final), alternating, Netflix browser on
  echo "== Live TV main vs this build, alternating (${SECS}s each) =="
  for round in 1 2; do
    start_baird true $HOME/baird-baseline; measure "main, round $round"
    start_baird true $HOME/baird-final; measure "this build, round $round"
  done
  exit 0
fi

if [ "$MODE" = baseline ]; then
  B=$HOME/baird-baseline
  echo "== Live TV baseline (${SECS}s each) =="
  start_baird true $B; measure "main, Netflix browser on"
  start_baird true $B --setenv=NETFLIX_USER=; measure "main, Netflix browser off"
  start_baird true $HOME/baird-mine --setenv=NETFLIX_USER=; measure "test build, Netflix browser off"
  exit 0
fi

echo "== 1. Transparency with a Chromium behind Baird =="
start_baird true
sleep 8
bash $T/pi_harness.sh /tmp/baird-ab-profile https://example.com >/dev/null
sleep 6
echo "active window: $(xdotool getactivewindow getwindowname 2>/dev/null)"
grim $OUT/1-before.png
sudo python3 $T/pi_keys.py F9 >/dev/null; sleep 2
grim $OUT/2-f9-transparent.png
echo "active window after F9: $(xdotool getactivewindow getwindowname 2>/dev/null)"
sudo python3 $T/pi_keys.py F9 >/dev/null; sleep 2
grim $OUT/3-f9-again.png
journalctl -t baird-ab --since "$START" --no-pager | grep -E "SPIKE|Focus" | tail -5
systemctl --user stop baird-probe.service 2>/dev/null

echo "== 2. Live TV cost (${SECS}s each) =="
start_baird false; measure "hint off"
start_baird true; measure "hint on"
