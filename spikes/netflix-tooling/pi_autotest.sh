#!/bin/bash
# Runs ON THE PI. Unattended Baird test with screenshots; always restores baird.service.
#   usage: pi_autotest.sh [netflix|netflix-search] [test|installed] [max-shots]
#     netflix         -> the playback spike (BAIRD_AUTOTEST=netflix)
#     netflix-search  -> the search spike   (BAIRD_AUTOTEST=netflix-search)
#     test            -> run the test build in ~/baird-test (copied there by deploy_test_build.sh)
#     installed       -> run the brew-installed build via its wrapper (/home/linuxbrew/.linuxbrew/bin/baird)
#   KEYS="ENTER RIGHT ESC" (optional): after the RESULT line, press each key through uinput (pi_keys.py, sudo) and take
#     two screenshots 0.5 s apart after each one (hud-NN-KEY-{a,b}.png), so motion and the HUD state can be checked.
# Output: ~/baird-test/shots/{shot-NNN,final-N}.png every 3 s, focus.log (which window had focus), autotest.log (journal lines).
set -u
TEST=${1:-netflix}; WHICH=${2:-test}; MAX=${3:-100}
export XDG_RUNTIME_DIR=/run/user/$(id -u) WAYLAND_DISPLAY=wayland-0 DISPLAY=:0
T=$HOME/baird-test; SH=$T/shots; mkdir -p $SH; rm -f $SH/*.png $SH/*.log
restore() {
  systemctl --user stop baird-autotest.service 2>/dev/null; sleep 2
  pkill -f "class=baird-netflix" 2>/dev/null          # never leave a signed-in browser behind
  systemctl --user reset-failed baird.service baird-autotest.service 2>/dev/null
  systemctl --user start baird.service
  echo "restored baird.service: $(systemctl --user is-active baird.service)"
}
trap restore EXIT
if [ "$WHICH" = installed ]; then
  BIN=/home/linuxbrew/.linuxbrew/bin/baird; LIBENV=""          # the wrapper sets LD_LIBRARY_PATH itself
else
  BIN=$T/Baird; chmod +x "$BIN"; LIBENV="--setenv=LD_LIBRARY_PATH=$T"
fi
START=$(date +"%Y-%m-%d %H:%M:%S")
systemctl --user stop baird.service; sleep 2
systemctl --user reset-failed baird-autotest.service 2>/dev/null   # a leftover unit blocks systemd-run
echo "baird.service stopped for the test; running $WHICH build with BAIRD_AUTOTEST=$TEST"
systemd-run --user --unit=baird-autotest --collect --property=SyslogIdentifier=baird-autotest \
  --setenv=DISPLAY=:0 --setenv=BAIRD_FULLSCREEN=True --setenv=XAUTHORITY=$HOME/.Xauthority \
  --setenv=WAYLAND_DISPLAY=wayland-0 --setenv=XDG_RUNTIME_DIR=$XDG_RUNTIME_DIR \
  $LIBENV --setenv=BAIRD_AUTOTEST=$TEST "$BIN" >/dev/null
for i in $(seq 1 "$MAX"); do
  sleep 3
  grim $SH/shot-$(printf %03d $i).png 2>/dev/null
  echo "$(date +%T) shot-$(printf %03d $i): active=$(xdotool getactivewindow 2>/dev/null) chromium=$(xdotool search --class baird-netflix 2>/dev/null | tr '\n' ' ')" >> $SH/focus.log
  # NB: the system journal (no --user): the transient unit's output lands there.
  if journalctl -t baird-autotest --since "$START" --no-pager 2>/dev/null | grep -q "\[AutoTest\] RESULT"; then
    if [ -n "${KEYS:-}" ]; then
      sleep 6; n=0
      for k in START $KEYS; do
        [ "$k" != START ] && sudo python3 $T/pi_keys.py "$k" >> $SH/focus.log 2>&1 && sleep 2.5
        tag=$(printf %02d $n)-$k
        grim $SH/hud-$tag-a.png; sleep 0.5; grim $SH/hud-$tag-b.png
        echo "$(date +%T) hud-$tag: active=$(xdotool getactivewindow 2>/dev/null) chromium=$(xdotool search --class baird-netflix 2>/dev/null | tr '\n' ' ')" >> $SH/focus.log
        n=$((n+1))
      done
    fi
    sleep 15
    for j in 1 2 3 4; do grim $SH/final-$j.png; echo "$(date +%T) final-$j: active=$(xdotool getactivewindow 2>/dev/null)" >> $SH/focus.log; sleep 4; done
    break
  fi
done
journalctl -t baird-autotest --since "$START" --no-pager 2>/dev/null | grep -E "AutoTest|NetflixSpike|NetflixHud|NetflixSearch|YtDlpTempDir" | cut -c1-300 > $SH/autotest.log
echo "--- journal ---"; cat $SH/autotest.log
