#!/usr/bin/env python3
"""Runs ON THE PI with sudo. Presses keys through a virtual uinput keyboard, so they go through labwc like the real remote
(xdotool sends XTEST events straight into Xwayland and skips the compositor, so it cannot test focus). Stdlib only.
  usage: sudo python3 pi_keys.py ENTER RIGHT RIGHT ESC      (keys: see KEYS; 'sleep:N' waits N seconds)
"""
import fcntl, os, struct, sys, time

KEYS = {"ESC": 1, "BACKSPACE": 14, "ENTER": 28, "SPACE": 57, "UP": 103, "LEFT": 105, "RIGHT": 106, "DOWN": 108, "F9": 67, "PAGEUP": 104,
        "BACK": 158, "PLAYPAUSE": 164}
EV_SYN, EV_KEY, SYN_REPORT = 0, 1, 0
UI_SET_EVBIT, UI_SET_KEYBIT, UI_DEV_CREATE, UI_DEV_DESTROY = 0x40045564, 0x40045565, 0x5501, 0x5502


def emit(fd, etype, code, value):
    os.write(fd, struct.pack("llHHi", 0, 0, etype, code, value))


def main(args):
    fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
    fcntl.ioctl(fd, UI_SET_EVBIT, EV_KEY)
    for code in KEYS.values():
        fcntl.ioctl(fd, UI_SET_KEYBIT, code)
    # struct uinput_user_dev: name[80], input_id{bustype,vendor,product,version}, ff_effects_max, abs arrays (4 x 64 int)
    os.write(fd, struct.pack("80sHHHHi", b"baird-test-keys", 0x03, 0x1234, 0x5678, 1, 0) + bytes(4 * 64 * 4))
    fcntl.ioctl(fd, UI_DEV_CREATE)
    time.sleep(1.0)  # let the compositor pick up the new device
    try:
        for a in args:
            if a.startswith("sleep:"):
                time.sleep(float(a[6:]))
                continue
            code = KEYS[a.upper()]
            emit(fd, EV_KEY, code, 1); emit(fd, EV_SYN, SYN_REPORT, 0)
            time.sleep(0.05)
            emit(fd, EV_KEY, code, 0); emit(fd, EV_SYN, SYN_REPORT, 0)
            print(f"{time.strftime('%T')} pressed {a}", flush=True)
            time.sleep(0.3)
    finally:
        time.sleep(0.2)
        fcntl.ioctl(fd, UI_DEV_DESTROY)
        os.close(fd)


if __name__ == "__main__":
    main(sys.argv[1:])
