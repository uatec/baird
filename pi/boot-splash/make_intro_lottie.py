#!/usr/bin/env python3
"""Writes Baird/Assets/Lottie/baird-intro.json: a Nipkow disc (Baird's mechanical-TV scanning disc: holes on one turn of a
spiral) that fades in around the wordmark, turns, and fades as Home arrives. 1920x1080, 60 fps, 2.5 s. The wordmark itself
is drawn by Baird (same font/size/place as the boot splash), so the boot splash is the animation's first frame exactly.
  usage: python3 pi/boot-splash/make_intro_lottie.py
"""
import json, math, os

FPS, FRAMES, W, H = 60, 150, 1920, 1080
HOLES, R0, R1 = 36, 330, 430           # one turn of the spiral, inner to outer radius
EASE_O, EASE_I = {"x": [0.25], "y": [0.1]}, {"x": [0.25], "y": [1]}

def kf(points):
    """Animated property from [(frame, value-list)], eased (0.25,0.1,0.25,1) as Baird's motion is."""
    out = []
    for n, (t, v) in enumerate(points):
        k = {"t": t, "s": v}
        if n < len(points) - 1: k.update({"o": EASE_O, "i": EASE_I})
        out.append(k)
    return {"a": 1, "k": out}

def static(v): return {"a": 0, "k": v}

groups = []
for i in range(HOLES):
    a = 2 * math.pi * i / HOLES - math.pi / 2
    r = R0 + (R1 - R0) * i / HOLES
    d = 10 + 8 * i / HOLES
    groups.append({"ty": "gr", "nm": f"hole{i}", "it": [
        {"ty": "el", "p": static([round(r * math.cos(a), 2), round(r * math.sin(a), 2)]), "s": static([d, d])},
        {"ty": "fl", "c": static([1, 1, 1, 1]), "o": static(round(35 + 65 * i / HOLES))},
        {"ty": "tr", "p": static([0, 0]), "a": static([0, 0]), "s": static([100, 100]), "r": static(0), "o": static(100)},
    ]})

layer = {
    "ddd": 0, "ind": 1, "ty": 4, "nm": "nipkow disc", "sr": 1, "ao": 0, "bm": 0, "ip": 0, "op": FRAMES, "st": 0,
    "ks": {
        "o": kf([(0, [0]), (36, [100]), (105, [100]), (FRAMES, [0])]),
        "r": kf([(0, [-40]), (FRAMES, [80])]),
        "p": static([W / 2, H / 2, 0]), "a": static([0, 0, 0]),
        "s": kf([(0, [92, 92, 100]), (FRAMES, [112, 112, 100])]),
    },
    "shapes": groups,
}
doc = {"v": "5.7.4", "fr": FPS, "ip": 0, "op": FRAMES, "w": W, "h": H, "nm": "baird-intro", "ddd": 0, "assets": [], "layers": [layer]}
out = os.path.join(os.path.dirname(__file__), "..", "..", "Baird", "Assets", "Lottie", "baird-intro.json")
with open(out, "w") as f: json.dump(doc, f, separators=(",", ":"))
print("wrote", os.path.normpath(out))
