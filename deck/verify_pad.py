#!/usr/bin/env python3
"""Prove the virtual pad emits real kernel input events.

Reads the pad's own event device while driving it over HTTP. Checking the
agent's self-reported state would prove nothing - it has to be observed from
the other side of the kernel, exactly as a game would see it.
"""
import json
import os
import struct
import sys
import threading
import time
import urllib.request

# Reach the pad the same way the pad decides to be reachable, rather than
# assuming loopback: it binds its Tailscale address now, so a hardcoded
# 127.0.0.1 gets connection-refused. Importing deckpad keeps one source of
# truth - it only defines things at import time, nothing is opened.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from deckpad import TOKEN_PATH, _default_host   # noqa: E402

BASE = os.environ.get("DECKPAD_URL") or "http://%s:8792" % _default_host()
with open(TOKEN_PATH) as _f:
    TOKEN = _f.read().strip()

EV_KEY, EV_ABS = 0x01, 0x03
BTN_A, BTN_B = 0x130, 0x131
ABS_X = 0x00
SIZE = struct.calcsize("llHHi")


def find_event_device():
    """Handlers appear as 'H: Handlers=event11 js1', so the eventN token is
    glued to 'Handlers=' - splitting on whitespace alone never matches it."""
    import re
    with open("/proc/bus/input/devices") as f:
        blocks = f.read().split("\n\n")
    for block in blocks:
        if "Kuroko Virtual Pad" not in block:
            continue
        m = re.search(r"\bevent(\d+)\b", block)
        if m:
            return "/dev/input/event" + m.group(1)
    return None


dev = find_event_device()
print("event device:", dev)
if not dev:
    raise SystemExit("pad not found in /proc/bus/input/devices")

seen = []


def reader():
    with open(dev, "rb") as f:
        end = time.time() + 6
        while time.time() < end:
            data = f.read(SIZE)
            if not data:
                break
            _, _, etype, code, value = struct.unpack("llHHi", data)
            if etype == EV_KEY:
                seen.append(("KEY", hex(code), value))
            elif etype == EV_ABS and code == ABS_X:
                seen.append(("ABS_X", hex(code), value))


t = threading.Thread(target=reader, daemon=True)
t.start()
time.sleep(0.5)


def post(path, payload):
    req = urllib.request.Request(BASE + path,
                                 data=json.dumps(payload).encode(),
                                 headers={"Content-Type": "application/json",
                                          "X-Deckpad-Token": TOKEN},
                                 method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=5).read())


print("press a ->", post("/press", {"button": "a", "ms": 60}))
time.sleep(0.3)
print("press b ->", post("/press", {"button": "b", "ms": 60}))
time.sleep(0.3)
print("stick   ->", post("/axis", {"axis": "lx", "value": -1.0}))
time.sleep(0.3)
post("/release_all", {})
time.sleep(1.0)

print("\nkernel events observed:")
for e in seen:
    print("  ", e)

keys = [e for e in seen if e[0] == "KEY"]
a_down = ("KEY", hex(BTN_A), 1) in seen
a_up = ("KEY", hex(BTN_A), 0) in seen
b_down = ("KEY", hex(BTN_B), 1) in seen
stick = any(e[0] == "ABS_X" and e[2] != 0 for e in seen)

print("\nRESULT: A press/release=%s/%s  B press=%s  stick moved=%s  (total key events=%d)"
      % (a_down, a_up, b_down, stick, len(keys)))
print("VERDICT:", "PASS" if (a_down and a_up and b_down and stick) else "FAIL")
