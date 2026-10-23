"""Minimal Chrome DevTools Protocol client, stdlib only (neither the Pi nor the dev PC has a websocket package).

    ssh -o HostKeyAlias=baird -N -L 9557:127.0.0.1:9557 baird.local &      # tunnel to pi_harness.sh's Chromium
    python3 -c 'import sys; sys.path.insert(0,"spikes/netflix-tooling"); from cdpmini import CDP, STATE; c=CDP(9557); print(c.eval(STATE))'

Use port 9222 to attach READ-ONLY to Baird's own Netflix browser while a test runs (9223 for the search spike).
"""
import socket, base64, os, json, struct, urllib.request


class CDP:
    def __init__(self, port):
        tabs = json.load(urllib.request.urlopen(f"http://127.0.0.1:{port}/json"))
        ws = [t for t in tabs if t["type"] == "page"][0]["webSocketDebuggerUrl"]
        host, path = ws.split("://")[1].split("/", 1)
        h, p = host.split(":")
        self.s = socket.create_connection((h, int(p)), timeout=30)
        key = base64.b64encode(os.urandom(16)).decode()
        self.s.sendall(f"GET /{path} HTTP/1.1\r\nHost: {host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
                       f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n".encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.s.recv(1)
        self.id = 0
        self.buf = b""

    def _send(self, text):
        d = text.encode(); mask = os.urandom(4); n = len(d)
        hdr = b"\x81" + (bytes([0x80 | n]) if n < 126 else b"\xfe" + struct.pack(">H", n) if n < 65536 else b"\xff" + struct.pack(">Q", n))
        self.s.sendall(hdr + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(d)))

    def recv(self):
        """Next message (response or event) as a dict. Set self.s.settimeout() to poll for events."""
        def rd(n):
            while len(self.buf) < n:
                c = self.s.recv(65536)
                if not c:
                    raise EOFError
                self.buf += c
            o, self.buf = self.buf[:n], self.buf[n:]
            return o
        out = b""
        while True:
            b1, b2 = rd(2); fin = b1 & 0x80; ln = b2 & 0x7f
            if ln == 126: ln = struct.unpack(">H", rd(2))[0]
            elif ln == 127: ln = struct.unpack(">Q", rd(8))[0]
            out += rd(ln)
            if fin:
                return json.loads(out.decode())

    def call(self, method, params=None):
        self.id += 1; me = self.id
        self._send(json.dumps({"id": me, "method": method, "params": params or {}}))
        while True:
            m = self.recv()
            if m.get("id") == me:
                return m

    def eval(self, js):
        r = self.call("Runtime.evaluate", {"expression": js, "returnByValue": True, "awaitPromise": True})
        return r.get("result", {}).get("result", {}).get("value")


# Page state at a glance: membership, video element, Netflix error code, path.
STATE = """(function(){const v=document.querySelector('video');let ms=null;
 try{ms=window.netflix.reactContext.models.userInfo.data.membershipStatus}catch(e){}
 return JSON.stringify({member:ms,video:v?{t:+v.currentTime.toFixed(1),paused:v.paused,w:v.videoWidth}:null,
   err:(document.body.innerText.match(/Error Code\\s*([A-Z0-9._-]+)/)||[])[1]||null,path:location.pathname.slice(0,40)})})()"""
