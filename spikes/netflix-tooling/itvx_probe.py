"""Drive the Pi probe Chromium (pi_harness.sh, CDP 9557 through an ssh tunnel) for ITVX exploration.
usage: itvx_probe.py <cdpmini-dir> <cmd> [args]
  state                 url, title, text, page-data keys, video state
  go <url>              navigate and wait for load, then state
  nextdata <file>       save __NEXT_DATA__ JSON
  shot <file.png>       Page.captureScreenshot
  eval <js>             evaluate JS, print JSON
"""
import sys, json, time, base64
sys.path.insert(0, sys.argv[1])
from cdpmini import CDP

STATE = r"""JSON.stringify((function(){
  const nd = document.getElementById('__NEXT_DATA__');
  let keys = null; try { keys = nd ? Object.keys(JSON.parse(nd.textContent).props.pageProps) : null } catch(e) { keys = 'unparsable' }
  const vids = [...document.querySelectorAll('video')].map(v => ({src: (v.currentSrc||'').slice(0,40), rs: v.readyState, t: v.currentTime, d: v.duration, paused: v.paused, w: v.videoWidth, h: v.videoHeight}));
  return {url: location.href, title: document.title, pageProps: keys, videos: vids,
          text: (document.body ? document.body.innerText : '').replace(/\s+/g,' ').slice(0, 600)};
})())"""

def ev(c, js):
    r = c.call("Runtime.evaluate", {"expression": js, "returnByValue": True, "awaitPromise": True})
    res = r.get("result", {})
    if "exceptionDetails" in res: return {"exception": res["exceptionDetails"].get("text"), "detail": str(res["exceptionDetails"].get("exception", {}).get("description"))[:300]}
    return res.get("result", {}).get("value")

c = CDP(9557)
cmd = sys.argv[2]
if cmd == "go":
    c.call("Page.enable"); c.call("Page.navigate", {"url": sys.argv[3]}); time.sleep(8)
    cmd = "state"
if cmd == "state":
    v = ev(c, STATE); print(json.dumps(json.loads(v) if isinstance(v, str) else v, indent=1))
elif cmd == "nextdata":
    v = ev(c, "document.getElementById('__NEXT_DATA__') ? document.getElementById('__NEXT_DATA__').textContent : null")
    if v: open(sys.argv[3], "w").write(v); print("saved", len(v))
    else: print("no __NEXT_DATA__")
elif cmd == "shot":
    r = c.call("Page.captureScreenshot", {"format": "png"})
    open(sys.argv[3], "wb").write(base64.b64decode(r["result"]["data"])); print("saved", sys.argv[3])
elif cmd == "eval":
    print(json.dumps(ev(c, sys.argv[3]), indent=1)[:6000])
