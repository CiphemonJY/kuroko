#!/usr/bin/env python3
"""deckpad - a virtual gamepad that runs ON the Steam Deck.

Why this instead of Bluetooth or a USB cable:
  * Windows cannot be a Bluetooth HID peripheral (its stack is host-only), so
    the PC cannot pretend to be a controller.
  * A USB-C to USB-C cable joins two USB HOSTS. Neither side can present itself
    as a device, so nothing happens.
  * The Deck is a Linux PC, so it can simply create a gamepad locally through
    /dev/uinput. The model then drives it over the network. No hardware, and
    lower latency than Bluetooth.

Uses raw ioctls via ctypes rather than python-evdev: SteamOS ships a read-only
root filesystem, so anything requiring a package install is a liability.

It advertises the Xbox 360 pad USB IDs because Steam recognises that layout
without any per-device configuration.

Run on the Deck (needs access to /dev/uinput, hence sudo):
    sudo python3 deckpad.py --port 8792

Then from the PC:
    curl -X POST http://<deck>:8792/press -d '{"button":"a"}'
"""

import argparse
import ctypes
import fcntl
import hmac
import json
import os
import secrets
import struct
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# The PC has to reach this across the tailnet, so it cannot bind to loopback -
# but the tailnet carries other people's machines, and an unauthenticated
# endpoint here means anyone on it can press buttons on this device. A shared
# token is the minimum that makes the open bind defensible.
TOKEN_PATH = os.path.expanduser("~/.deckpad_token")


def load_or_create_token():
    """Reuse the token across restarts so the PC side doesn't need rewiring."""
    try:
        with open(TOKEN_PATH) as f:
            tok = f.read().strip()
            if tok:
                return tok
    except FileNotFoundError:
        pass
    tok = secrets.token_urlsafe(24)
    fd = os.open(TOKEN_PATH, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w") as f:
        f.write(tok)
    return tok


TOKEN = ""

# --- uinput constants -------------------------------------------------------
UI_SET_EVBIT, UI_SET_KEYBIT, UI_SET_ABSBIT = 0x40045564, 0x40045565, 0x40045567
UI_DEV_CREATE, UI_DEV_DESTROY = 0x5501, 0x5502
EV_SYN, EV_KEY, EV_ABS = 0x00, 0x01, 0x03
SYN_REPORT = 0

BUTTONS = {
    "a": 0x130, "b": 0x131, "x": 0x134, "y": 0x133,
    "lb": 0x136, "rb": 0x137,
    "back": 0x13a, "select": 0x13a, "start": 0x13b, "menu": 0x13b,
    "guide": 0x13c, "steam": 0x13c,
    "l3": 0x13d, "r3": 0x13e,
}
# Axis: name -> (code, min, max)
AXES = {
    "lx": (0x00, -32768, 32767), "ly": (0x01, -32768, 32767),
    "rx": (0x03, -32768, 32767), "ry": (0x04, -32768, 32767),
    "lt": (0x02, 0, 255),        "rt": (0x05, 0, 255),
    "hatx": (0x10, -1, 1),       "haty": (0x11, -1, 1),
}
DPAD = {"up": ("haty", -1), "down": ("haty", 1), "left": ("hatx", -1), "right": ("hatx", 1)}


class VirtualPad:
    def __init__(self, name="ShadowCast Virtual Pad"):
        self.fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
        self._lock = threading.Lock()

        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_KEY)
        fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_ABS)
        for code in set(BUTTONS.values()):
            fcntl.ioctl(self.fd, UI_SET_KEYBIT, code)
        for code, _, _ in AXES.values():
            fcntl.ioctl(self.fd, UI_SET_ABSBIT, code)

        # struct uinput_user_dev: name[80], input_id(4xu16), ff_effects_max,
        # then absmax/absmin/absfuzz/absflat, 64 int32 each.
        absmax = [0] * 64
        absmin = [0] * 64
        for code, lo, hi in AXES.values():
            absmin[code], absmax[code] = lo, hi
        dev = struct.pack(
            "80sHHHHi" + "64i" * 4,
            name.encode()[:79],
            0x03, 0x045E, 0x028E, 0x0110,   # BUS_USB, Microsoft, Xbox 360 pad
            0,
            *absmax, *absmin, *([0] * 64), *([0] * 64),
        )
        os.write(self.fd, dev)
        fcntl.ioctl(self.fd, UI_DEV_CREATE)
        time.sleep(0.3)   # let udev/Steam notice the new device

        self.state = {k: 0 for k in AXES}
        self.pressed = set()

    def _emit(self, etype, code, value):
        # input_event on 64-bit: timeval(2 longs) + type + code + value
        os.write(self.fd, struct.pack("llHHi", 0, 0, etype, code, value))

    def _sync(self):
        self._emit(EV_SYN, SYN_REPORT, 0)

    def button(self, name, down):
        code = BUTTONS.get(name.lower())
        if code is None:
            raise ValueError("unknown button: %s (have: %s)" % (name, ", ".join(sorted(BUTTONS))))
        with self._lock:
            self._emit(EV_KEY, code, 1 if down else 0)
            self._sync()
            self.pressed.add(name) if down else self.pressed.discard(name)

    def axis(self, name, value):
        """value is -1..1 for sticks/dpad, 0..1 for triggers."""
        if name.lower() in DPAD:
            axis, v = DPAD[name.lower()]
            return self.axis_raw(axis, v)
        code, lo, hi = AXES[name.lower()]
        v = int(round((value + 1) / 2 * (hi - lo) + lo)) if lo < 0 else int(round(value * hi))
        self.axis_raw(name, max(lo, min(hi, v)))

    def axis_raw(self, name, raw):
        code, lo, hi = AXES[name.lower()]
        raw = max(lo, min(hi, int(raw)))
        with self._lock:
            self._emit(EV_ABS, code, raw)
            self._sync()
            self.state[name.lower()] = raw

    def tap(self, name, ms=80):
        self.button(name, True)
        time.sleep(max(0.01, ms / 1000.0))
        self.button(name, False)

    def release_all(self):
        """Safety net: a model that crashes mid-hold must not leave a stick
        jammed or a button stuck down."""
        for b in list(self.pressed):
            try: self.button(b, False)
            except Exception: pass
        for a in AXES:
            try: self.axis_raw(a, 0)
            except Exception: pass

    def close(self):
        try:
            self.release_all()
            fcntl.ioctl(self.fd, UI_DEV_DESTROY)
        finally:
            os.close(self.fd)


PAD = None


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass   # keep the console quiet

    def _json(self, code, obj):
        body = json.dumps(obj).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _body(self):
        n = int(self.headers.get("Content-Length") or 0)
        return json.loads(self.rfile.read(n) or b"{}")

    def _authed(self):
        """compare_digest, not ==, so the check is not timing-distinguishable."""
        got = self.headers.get("X-Deckpad-Token", "")
        if hmac.compare_digest(got, TOKEN):
            return True
        self._json(401, {"error": "missing or bad X-Deckpad-Token header"})
        return False

    def do_GET(self):
        if not self._authed():
            return
        if self.path.startswith("/status"):
            self._json(200, {"ok": True, "device": "ShadowCast Virtual Pad",
                             "buttons": sorted(BUTTONS), "axes": sorted(AXES),
                             "dpad": sorted(DPAD), "pressed": sorted(PAD.pressed),
                             "axis_state": PAD.state})
        else:
            self._json(404, {"error": "try /status, /press, /hold, /axis, /sequence, /release_all"})

    def do_POST(self):
        if not self._authed():
            return
        try:
            b = self._body()
            p = self.path.rstrip("/")

            if p == "/press":
                PAD.tap(b["button"], int(b.get("ms", 80)))
            elif p == "/hold":
                PAD.button(b["button"], bool(b.get("down", True)))
            elif p == "/axis":
                PAD.axis(b["axis"], float(b.get("value", 0)))
            elif p == "/release_all":
                PAD.release_all()
            elif p == "/sequence":
                for step in b.get("steps", []):
                    kind = step.get("type", "press")
                    if kind == "press":
                        PAD.tap(step["button"], int(step.get("ms", 80)))
                    elif kind == "hold":
                        PAD.button(step["button"], bool(step.get("down", True)))
                    elif kind == "axis":
                        PAD.axis(step["axis"], float(step.get("value", 0)))
                    elif kind == "wait":
                        time.sleep(min(5.0, float(step.get("ms", 100)) / 1000.0))
                    else:
                        raise ValueError("unknown step type: %s" % kind)
            else:
                return self._json(404, {"error": "unknown endpoint %s" % p})

            self._json(200, {"ok": True})
        except Exception as e:
            self._json(400, {"ok": False, "error": "%s: %s" % (type(e).__name__, e)})


def main():
    global PAD, TOKEN
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8792)
    ap.add_argument("--host", default="0.0.0.0",
                    help="0.0.0.0 so the PC can reach it; use 127.0.0.1 to lock it down")
    args = ap.parse_args()

    if not os.access("/dev/uinput", os.W_OK):
        raise SystemExit("Cannot write /dev/uinput - run with sudo, or add a udev rule.")

    TOKEN = load_or_create_token()
    PAD = VirtualPad()
    print("virtual gamepad created; listening on %s:%d" % (args.host, args.port))
    print("Steam should now list 'ShadowCast Virtual Pad' as a controller.")
    print("auth token (%s): %s" % (TOKEN_PATH, TOKEN))
    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        PAD.close()


if __name__ == "__main__":
    main()
