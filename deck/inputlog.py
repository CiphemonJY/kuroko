#!/usr/bin/env python3
"""Record REAL human controller input on the Deck, as demonstration data.

The other half of the loop. deckpad INJECTS input; this CAPTURES it, so an hour
of ordinary play becomes a labelled (frame, action) dataset for behavioural
cloning instead of evaporating.

Writes JSON Lines to stdout, so the PC can collect a session over ssh with no
file to copy back and nothing left running on the Deck:

    ssh deck@steamdeck 'python3 -u inputlog.py' > session.jsonl

That also sidesteps SteamOS's KillUserProcesses=True, which kills anything
backgrounded the moment the ssh session ends - a foreground pipe is the one
shape that survives for as long as you want it to.

WHICH DEVICES ARE LOGGED:
  1. has a `js` handler   -> it is a joystick, not a lid switch
  2. advertises BTN_SOUTH -> it is a gamepad, not the motion sensors
  3. is not named as OUR pad (deckpad.PAD_NAME)

Test 3 excludes our own injected pad. Logging that would record the agent's
actions as if a human made them and train the next policy on its own output - a
silent, self-confirming corruption you would not notice for weeks.

It is done BY NAME, and that detail is the whole lesson of this file. The
obvious test - "skip virtual devices, keep real hardware" - is wrong on a Steam
Deck, and wrong in the exact situation that matters. In Game Mode, Steam takes
the built-in controller exclusively (its raw node DISAPPEARS, mid-read, with
ENODEV) and publishes its own `Microsoft X-Box 360 pad 0` through uinput. So the
pad you must record is virtual, sits at /devices/virtual/ with an empty Phys,
and is indistinguishable in kind from ours. Filtering on virtualness would find
nothing during actual gameplay while looking perfectly healthy in Desktop Mode.

Devices are re-scanned while running for the same reason: the set changes at
precisely the moment you start playing.

Timestamps come from the KERNEL (the input_event timeval), not from time.time()
at read: the kernel stamps at the moment the input happens, so userspace
scheduling jitter does not smear the labels. The clock is CLOCK_REALTIME, i.e.
directly comparable to a wall-clock recording made on another machine - see
record-session.ps1, which measures the Deck<->PC clock offset so the two can
actually be aligned rather than assumed to agree.

No third-party packages, raw ioctl-free reads only: SteamOS ships a read-only
root filesystem, so anything needing pip is a liability (same reason deckpad
uses ctypes rather than python-evdev).
"""

import argparse
import json
import os
import re
import select
import struct
import sys
import time

EV_SYN, EV_KEY, EV_ABS = 0x00, 0x01, 0x03
EVENT_SIZE = struct.calcsize("llHHi")   # timeval(2 longs) + type + code + value
DEVICES = "/proc/bus/input/devices"
BTN_SOUTH = 0x130                       # every gamepad has an "A"; nothing else does

# Code -> name. Imported from deckpad so there is ONE definition of the mapping;
# deckpad carries aliases (back/select, start/menu, guide/steam) so pick a
# canonical name per code, otherwise dict order would decide it for us and the
# dataset's column names could change between runs.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
try:
    from deckpad import AXES, BUTTONS, PAD_NAME
except ImportError:      # logger must still run if it is copied out on its own
    PAD_NAME = "Kuroko Virtual Pad"
    BUTTONS = {"a": 0x130, "b": 0x131, "x": 0x134, "y": 0x133, "lb": 0x136,
               "rb": 0x137, "back": 0x13a, "start": 0x13b, "guide": 0x13c,
               "l3": 0x13d, "r3": 0x13e}
    AXES = {"lx": (0x00, -32768, 32767), "ly": (0x01, -32768, 32767),
            "rx": (0x03, -32768, 32767), "ry": (0x04, -32768, 32767),
            "lt": (0x02, 0, 255), "rt": (0x05, 0, 255),
            "hatx": (0x10, -1, 1), "haty": (0x11, -1, 1)}

CANONICAL = {0x13a: "back", 0x13b: "start", 0x13c: "guide"}
CODE_TO_BUTTON = {code: name for name, code in sorted(BUTTONS.items())}
CODE_TO_BUTTON.update(CANONICAL)
CODE_TO_AXIS = {code: name for name, (code, _lo, _hi) in sorted(AXES.items())}


def parse_devices():
    """Yield (name, sysfs, handlers, key_bitmap_words) per /proc entry."""
    with open(DEVICES) as f:
        for block in f.read().split("\n\n"):
            if not block.strip():
                continue
            name = re.search(r'N: Name="(.*)"', block)
            hand = re.search(r"H: Handlers=(.*)", block)
            sysfs = re.search(r"S: Sysfs=(.*)", block)
            key = re.search(r"B: KEY=(.*)", block)
            if not (name and hand):
                continue
            # The bitmap prints its highest word FIRST, so reverse it before
            # indexing bit N as words[N // 64] >> (N % 64).
            words = [int(w, 16) for w in key.group(1).split()][::-1] if key else []
            yield (name.group(1), sysfs.group(1) if sysfs else "",
                   hand.group(1), words)


def has_bit(words, bit):
    i, off = bit // 64, bit % 64
    return i < len(words) and bool((words[i] >> off) & 1)


def discover(include_virtual=False):
    """Gamepads a human might be holding. See the docstring for why NOT virtualness."""
    found = []
    for name, sysfs, handlers, keys in parse_devices():
        ev = re.search(r"\b(event\d+)\b", handlers)
        if not ev:
            continue
        reasons = []
        if "js" not in handlers:
            reasons.append("no js handler")
        if not has_bit(keys, BTN_SOUTH):
            reasons.append("no BTN_SOUTH (not a gamepad)")
        if name == PAD_NAME and not include_virtual:
            reasons.append("this is OUR injected pad, not a human")
        if reasons:
            # Only report near-misses. A Deck has ~20 input devices (lid switch,
            # HDMI audio jacks, the power button) and listing why each one is not
            # a gamepad buries the single line that matters: that our own
            # injected pad was seen and deliberately skipped.
            if "js" in handlers or has_bit(keys, BTN_SOUTH):
                print(json.dumps({"kind": "skipped", "device": name,
                                  "why": reasons}), file=sys.stderr)
            continue
        found.append((name, "/dev/input/" + ev.group(1)))
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seconds", type=float, default=0,
                    help="stop after N seconds (default: until Ctrl-C / EOF)")
    ap.add_argument("--device", action="append", default=[],
                    help="log this event node instead of auto-discovering")
    ap.add_argument("--include-virtual", action="store_true",
                    help="ALSO log our own injected pad. Only for testing this "
                         "logger - it corrupts a human demonstration dataset.")
    ap.add_argument("--out", default="-", help="output file (default stdout)")
    args = ap.parse_args()

    explicit = bool(args.device)
    if explicit:
        pads = [(os.path.basename(d), d) for d in args.device]
        missing = [p for _n, p in pads if not os.path.exists(p)]
        if missing:
            # Event node NUMBERS are not stable - they are reassigned whenever
            # devices re-enumerate, which happens every time Steam takes or
            # releases the controller. Naming one by hand is how a run silently
            # became a FileNotFoundError that looked like "no input happened".
            raise SystemExit("no such device: %s - node numbers move around; "
                             "prefer auto-discovery" % ", ".join(missing))
    else:
        pads = discover(args.include_virtual)

    # Line buffering: SteamOS can kill this process at session end, and a block
    # -buffered file would lose the tail of the session.
    out = sys.stdout if args.out == "-" else open(args.out, "w", buffering=1)

    fds = {}

    def open_pads(pad_list):
        opened = []
        for name, path in pad_list:
            if any(p == path for _n, p in fds.values()):
                continue
            try:
                fds[os.open(path, os.O_RDONLY)] = (name, path)
                opened.append(name)
            except OSError as ex:
                print(json.dumps({"kind": "open_failed", "device": name,
                                  "path": path, "error": str(ex)}), file=sys.stderr)
        return opened

    open_pads(pads)
    if not fds and explicit:
        raise SystemExit("could not open any of the named devices")
    if not fds:
        print(json.dumps({"kind": "waiting", "note":
                          "no gamepad yet - will keep scanning (Steam publishes "
                          "its pad only once Game Mode or a game is running)"}),
              file=sys.stderr)

    session = {
        "kind": "session",
        "started_wall": time.time(),          # CLOCK_REALTIME, same base as events
        "started_mono": time.monotonic(),
        "host": os.uname().nodename,
        "devices": [{"name": n, "path": p} for n, p in pads],
        "note": "event times are kernel CLOCK_REALTIME; align to PC video via "
                "the offset measured by record-session.ps1",
    }
    print(json.dumps(session), file=out, flush=True)

    deadline = time.monotonic() + args.seconds if args.seconds else None
    last_scan = time.monotonic()
    count = 0
    try:
        while True:
            if deadline and time.monotonic() >= deadline:
                break
            # Re-scan periodically. The device set changes at exactly the moment
            # play starts: entering Game Mode makes Steam seize the built-in pad
            # (its node disappears) and publish a new virtual one.
            if not explicit and time.monotonic() - last_scan > 2.0:
                last_scan = time.monotonic()
                fresh = open_pads(discover(args.include_virtual))
                for n in fresh:
                    print(json.dumps({"kind": "device_added", "device": n,
                                      "t": time.time()}), file=out, flush=True)

            timeout = 0.5 if not deadline else max(0, deadline - time.monotonic())
            ready, _, _ = select.select(list(fds), [], [], min(0.5, timeout) or 0.5)
            for fd in ready:
                name, _path = fds[fd]
                try:
                    # evdev returns whole events; read several per syscall so
                    # high-rate stick motion is not one syscall per sample.
                    data = os.read(fd, EVENT_SIZE * 64)
                except OSError as ex:
                    # ENODEV: the device was taken away mid-read. Not an error to
                    # die on - it is what Steam does to the built-in pad when
                    # Game Mode starts, and crashing here turned a real capture
                    # into an empty file that read as "the user pressed nothing".
                    print(json.dumps({"kind": "device_lost", "device": name,
                                      "t": time.time(), "error": str(ex)}),
                          file=out, flush=True)
                    try: os.close(fd)
                    except OSError: pass
                    del fds[fd]
                    continue
                for off in range(0, len(data) - EVENT_SIZE + 1, EVENT_SIZE):
                    sec, usec, etype, code, value = struct.unpack(
                        "llHHi", data[off:off + EVENT_SIZE])
                    if etype == EV_KEY:
                        kind, label = "key", CODE_TO_BUTTON.get(code)
                    elif etype == EV_ABS:
                        kind, label = "abs", CODE_TO_AXIS.get(code)
                    else:
                        continue          # SYN/MSC: ordering is recoverable from
                                          # the timestamps, which events in one
                                          # SYN group share exactly.
                    # Unknown codes are KEPT under a hex name, never dropped -
                    # the real Deck pad exposes buttons our virtual one does not
                    # (back paddles, extra triggers), and silently discarding
                    # them would leave holes in the demonstrations that nothing
                    # downstream could detect.
                    print(json.dumps({
                        "t": sec + usec / 1e6,
                        "dev": name,
                        "kind": kind,
                        "code": label or ("0x%x" % code),
                        "known": label is not None,
                        "value": value,
                    }), file=out, flush=False)
                    count += 1
            out.flush()
    except KeyboardInterrupt:
        pass
    finally:
        print(json.dumps({"kind": "end", "ended_wall": time.time(),
                          "events": count}), file=out, flush=True)
        for fd in fds:
            os.close(fd)
        if out is not sys.stdout:
            out.close()


if __name__ == "__main__":
    main()
