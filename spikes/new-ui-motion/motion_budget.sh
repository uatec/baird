#!/bin/bash
# Runs ON THE PI (from ~/baird-new, with pi_keys.py beside it). Measures dropped video frames in the new UI while live TV
# plays, per scenario: bare video, Home over the video (moving every 2 s), the info bar (shown every 6 s), and the guide
# with its live preview (moving every 2 s). Each scenario runs 60 s; the first 20 s are discarded (start-up burst).
# Needs a Baird started with BAIRD_AV_LOG=true (logs [AvLog] every 5 s). Usage: bash motion_budget.sh run.log
set -u
LOG=${1:-run.log}
K(){ sudo python3 ~/baird-new/pi_keys.py "$@" >/dev/null 2>&1; }
mark(){ echo "[Motion] ==== $1 $(date +%s)" >> "$LOG"; }
# Start: Home → On now → tune the first channel; settle.
K ESC sleep:0.5 ESC sleep:0.5 DOWN sleep:0.5 ENTER; sleep 25
mark bare;  sleep 60
K ESC; sleep 1
mark home;  for i in $(seq 1 30); do K RIGHT; sleep 1.6; done
K DOWN sleep:0.5 ENTER; sleep 10        # back on the video (re-tunes the lit channel)
mark info;  for i in $(seq 1 10); do K ENTER; sleep 6; done
K ESC sleep:0.5 ESC; sleep 1; K UP UP UP sleep:0.5 RIGHT; sleep 5
mark guide; for i in $(seq 1 30); do if [ $((i % 6)) -lt 3 ]; then K RIGHT; else K LEFT; fi; sleep 1.6; done
mark end
# Summary: drops per scenario, discarding samples in the first 20 s after each mark.
python3 - "$LOG" <<'PY'
import re,sys
lines=open(sys.argv[1],errors="ignore").read().splitlines()
cur=None;t0=0;samples={};n=0
for l in lines:
    m=re.search(r"\[Motion\] ==== (\w+) (\d+)",l)
    if m: cur=m.group(1); n=0; continue
    m=re.search(r"\[AvLog\] screen=(\S+) vo_drops=(-?\d+) dec_drops=(-?\d+)",l)
    if m and cur and cur!="end":
        n+=1
        if n<=4: continue                     # 4 samples x 5 s = first 20 s
        samples.setdefault(cur,[]).append((m.group(1),int(m.group(2)),int(m.group(3))))
for k,v in samples.items():
    print(f"{k:6} samples={len(v):2} vo_drops={sum(x[1] for x in v):4} dec_drops={sum(x[2] for x in v):4} screens={sorted(set(x[0] for x in v))}")
PY
