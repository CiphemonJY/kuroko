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

The pad is DETACHED at rest: running this service creates no input device at
all. Nothing appears to Steam, and js0 stays with the Deck's own controller,
until something explicitly links it. That is the difference between a tool you
leave running and one you have to remember to turn off.

Run on the Deck (needs access to /dev/uinput, hence sudo):
    sudo python3 deckpad.py --port 8792

Then from the PC - link first, then drive:
    curl -X POST http://<deck>:8792/attach
    curl -X POST http://<deck>:8792/press -d '{"button":"a"}'
    curl -X POST http://<deck>:8792/detach
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

# The PC reaches this across the tailnet, so it cannot bind to loopback (see
# _default_host) - but the tailnet carries other people's machines, and an
# unauthenticated endpoint here means anyone on it can press buttons on this
# device. Narrowing the bind is not sufficient on its own; the token is what
# makes a non-loopback bind defensible.
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
    def __init__(self, name="Kuroko Virtual Pad"):
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
        # Clamp HERE: this is the trust boundary. A caller asking for a
        # 9-hour button hold is either broken or hostile, and the actuator
        # is physical - there is no undo once the game sees it.
        ms = max(10, min(5000, int(ms)))
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
PAD_LOCK = threading.Lock()
LAST_INPUT = 0.0
IDLE_DETACH_S = 900.0     # 15 min; see detach_if_idle


def attach(reason="explicit"):
    """Create the virtual pad. NOTHING exists on the Deck until this runs.

    Running the HTTP listener and owning a gamepad are deliberately separate
    acts. A permanently-present uinput pad is not free: it took js0 - ahead of
    the Deck's own controller on js1 - so a game assigning player 1 to the first
    joystick it enumerates hands player 1 to a pad nobody is holding, and Steam
    reshuffles controller defaults around a phantom device. The service can now
    sit running all day and be genuinely invisible until something links it.
    """
    global PAD, LAST_INPUT
    with PAD_LOCK:
        if PAD is None:
            PAD = VirtualPad()
            LAST_INPUT = time.time()
            print("pad ATTACHED (%s) - now visible to Steam as a controller" % reason,
                  flush=True)
        return PAD


def detach(reason="explicit"):
    """Destroy the virtual pad. Releases everything first - a detach that left a
    button held would jam it with no device left to un-jam it through."""
    global PAD
    with PAD_LOCK:
        if PAD is not None:
            try:
                PAD.close()
            finally:
                PAD = None
            print("pad DETACHED (%s) - js0 released" % reason, flush=True)


def detach_if_idle():
    """Auto-detach after a long quiet spell.

    The app detaches on close, but it cannot if it crashes or the network drops,
    and the whole point is that a stale pad must not squat on js0 forever. The
    timeout is deliberately generous: a model can legitimately think for minutes
    between inputs, and detaching mid-session would be worse than the problem.
    """
    while True:
        time.sleep(30)
        if PAD is not None and LAST_INPUT and (time.time() - LAST_INPUT) > IDLE_DETACH_S:
            detach("idle %.0fs" % IDLE_DETACH_S)


def require_pad():
    """Input endpoints need a pad; say so precisely rather than 500ing on None."""
    global LAST_INPUT
    if PAD is None:
        return None
    LAST_INPUT = time.time()
    return PAD


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
            pad = PAD
            self._json(200, {"ok": True, "device": "Kuroko Virtual Pad",
                             "attached": pad is not None,
                             "buttons": sorted(BUTTONS), "axes": sorted(AXES),
                             "dpad": sorted(DPAD),
                             # No pad means no held state to report - and no
                             # device on the Deck at all. Report empties rather
                             # than failing: "is it linked?" is the question
                             # /status exists to answer.
                             "pressed": sorted(pad.pressed) if pad else [],
                             "axis_state": pad.state if pad else {k: 0 for k in AXES},
                             "idle_detach_s": IDLE_DETACH_S})
        else:
            self._json(404, {"error": "try /status, /attach, /detach, /press, "
                                      "/hold, /axis, /sequence, /release_all"})

    def do_POST(self):
        if not self._authed():
            return
        try:
            b = self._body()
            p = self.path.rstrip("/")

            if p == "/attach":
                attach(b.get("reason", "api"))
                return self._json(200, {"ok": True, "attached": True})
            if p == "/detach":
                detach(b.get("reason", "api"))
                return self._json(200, {"ok": True, "attached": False})

            pad = require_pad()
            if pad is None:
                # Deliberately NOT auto-attaching. Creating the pad makes Steam
                # re-enumerate controllers, which is exactly the interruption
                # this lifecycle exists to prevent - so it never happens as a
                # side effect of an input arriving. Linking is its own act.
                return self._json(409, {
                    "ok": False, "attached": False,
                    "error": "controller not linked - POST /attach first "
                             "(or start deckpad with --auto-attach)"})

            if p == "/press":
                pad.tap(b["button"], int(b.get("ms", 80)))
            elif p == "/hold":
                pad.button(b["button"], bool(b.get("down", True)))
            elif p == "/axis":
                pad.axis(b["axis"], float(b.get("value", 0)))
            elif p == "/release_all":
                pad.release_all()
            elif p == "/sequence":
                for step in b.get("steps", []):
                    kind = step.get("type", "press")
                    if kind == "press":
                        pad.tap(step["button"], int(step.get("ms", 80)))
                    elif kind == "hold":
                        pad.button(step["button"], bool(step.get("down", True)))
                    elif kind == "axis":
                        pad.axis(step["axis"], float(step.get("value", 0)))
                    elif kind == "wait":
                        time.sleep(min(5.0, float(step.get("ms", 100)) / 1000.0))
                    else:
                        raise ValueError("unknown step type: %s" % kind)
            else:
                return self._json(404, {"error": "unknown endpoint %s" % p})

            self._json(200, {"ok": True})
        except Exception as e:
            self._json(400, {"ok": False, "error": "%s: %s" % (type(e).__name__, e)})



def _default_host():
    """This Deck's Tailscale address - or refuse to start.

    0.0.0.0 put a uinput bridge on every interface the Deck ever joins - a
    token was the only thing between a cafe network and synthetic input on this
    machine. The PC reaches us over Tailscale, so bind only there.

    Read the interface directly: the `tailscale` CLI is not installed here, and
    shelling out to a missing binary failed closed to 127.0.0.1, which is safe
    but unreachable.

    Exiting rather than falling back is the whole point, and it is not
    theoretical: on the 2026-08-11 boot the unit started before tailscale0
    existed, quietly bound 127.0.0.1, and looked healthy while the PC could not
    reach it - which got "fixed" by restarting it by hand with --host 0.0.0.0,
    undoing this entirely. A non-zero exit turns that into a visible failure
    that systemd's Restart=on-failure retries until Tailscale is up.
    """
    import socket
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        packed = fcntl.ioctl(s.fileno(), 0x8915,  # SIOCGIFADDR
                             struct.pack("256s", b"tailscale0"))
        return socket.inet_ntoa(packed[20:24])
    except OSError:
        raise SystemExit(
            "tailscale0 has no address - refusing to guess a bind address.\n"
            "0.0.0.0 would expose this uinput bridge to every network the Deck\n"
            "joins; 127.0.0.1 would look identical to 'working' while the PC\n"
            "could not reach it. Under systemd this retries until Tailscale is\n"
            "up. To override deliberately, pass --host."
        )
    finally:
        s.close()


def main():
    global PAD, TOKEN
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=8792)
    # Resolved AFTER parsing, not as default=_default_host(): argparse evaluates
    # defaults eagerly, so an explicit --host would still have to survive the
    # Tailscale lookup - and the override exists precisely for when it fails.
    ap.add_argument("--host", default=None,
                    help="default: this Deck's Tailscale address (refuses to start "
                         "if Tailscale is down). Pass 0.0.0.0 only if you actually "
                         "want every network the Deck joins to reach a uinput bridge.")
    ap.add_argument("--auto-attach", action="store_true",
                    help="create the pad at startup, as it used to. Off by "
                         "default: a pad that always exists takes js0 from the "
                         "Deck's own controller and makes Steam re-shuffle "
                         "controller defaults around a device nobody is holding.")
    ap.add_argument("--idle-detach", type=float, default=IDLE_DETACH_S,
                    help="auto-detach after this many idle seconds (0 disables)")
    args = ap.parse_args()
    host = args.host if args.host is not None else _default_host()

    if not os.access("/dev/uinput", os.W_OK):
        raise SystemExit("Cannot write /dev/uinput - run with sudo, or add a udev rule.")

    globals()["IDLE_DETACH_S"] = args.idle_detach
    TOKEN = load_or_create_token()
    if args.auto_attach:
        attach("--auto-attach")
    if args.idle_detach:
        threading.Thread(target=detach_if_idle, daemon=True).start()
    srv = ThreadingHTTPServer((host, args.port), Handler)
    # Report what the socket ACTUALLY bound, not what was asked for. The two can
    # differ silently - passing None binds every interface - and "which address
    # is this listening on" is the one property here worth getting right.
    bound_host, bound_port = srv.socket.getsockname()[:2]
    print("deckpad listening on %s:%d" % (bound_host, bound_port))
    if bound_host == "0.0.0.0":
        print("WARNING: bound to ALL interfaces - every network this Deck joins "
              "can reach a uinput bridge, with only the token in front of it.")
    print("pad is %s. Steam sees NO extra controller until it is attached."
          % ("ATTACHED" if PAD else "detached"))
    print("auth token: written to %s (not echoed)" % TOKEN_PATH)
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        detach("shutdown")   # not PAD.close(): PAD is None whenever nothing is
                             # linked, which is now the normal resting state.


if __name__ == "__main__":
    main()
