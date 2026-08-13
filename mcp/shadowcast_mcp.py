#!/usr/bin/env python3
"""ShadowCast MCP server - lets a vision model see the captured console screen.

Speaks MCP over stdio as plain JSON-RPC. No third-party packages: the protocol
surface needed here (initialize / tools/list / tools/call) is small, and a
dependency would have to be installed on every machine that runs this.

It does NOT open the capture device. The dongle is exclusive - while the viewer
is running nothing else can grab it - so frames are fetched from the viewer's
local HTTP API (127.0.0.1:8791). Start ShadowCast.exe first.

Tools
  get_screen        one frame as an image
  get_screen_burst  several frames over a span, for motion/what-just-happened
  get_status        resolution, fps, capture state

Register with an MCP client, e.g.:
  claude mcp add shadowcast -- python "<this file>"
"""

import base64
import json
import sys
import time
import urllib.error
import urllib.request

API = "http://127.0.0.1:8791"
# Controller input runs ON the Steam Deck (deck/deckpad.py) because Linux can
# create a virtual gamepad through /dev/uinput. Windows cannot be a Bluetooth
# HID peripheral and a USB-C host-to-host cable does nothing, so this is the
# route that actually works - and it is lower latency than Bluetooth.
PAD = ""            # set from SHADOWCAST_PAD, e.g. http://steamdeck:8792
PAD_TOKEN = ""      # set from SHADOWCAST_PAD_TOKEN (see ~/.deckpad_token on the Deck)
TIMEOUT = 15

# Frames are the whole point of this server, so a failure has to explain itself
# well enough that the caller knows whether to start the app, start the console,
# or fix a port - not just "request failed".
HINT = ("Could not reach ShadowCast at %s. Start ShadowCast.exe (the viewer must "
        "be running and capturing - it owns the capture device exclusively). "
        "If it runs on another port, set SHADOWCAST_API." % API)


def _get(path):
    with urllib.request.urlopen(API + path, timeout=TIMEOUT) as r:
        return r.read(), r.headers.get("Content-Type", "")


def fetch_frame(width=1280, quality=80):
    body, ctype = _get("/frame?w=%d&q=%d" % (width, quality))
    if "image" not in ctype:
        raise RuntimeError(body.decode("utf-8", "replace")[:300])
    return base64.b64encode(body).decode("ascii")


def fetch_status():
    body, _ = _get("/status")
    return json.loads(body.decode("utf-8"))


PAD_HINT = ("No controller configured. Run deck/deckpad.py on the Steam Deck "
            "(sudo python3 deckpad.py) and set SHADOWCAST_PAD to its address, "
            "e.g. http://steamdeck:8792")


def _pad_headers(extra=None):
    # The pad binds 0.0.0.0 (the PC must reach it across the tailnet), so it
    # requires a shared token - otherwise anyone on the tailnet could drive it.
    h = {"X-Deckpad-Token": PAD_TOKEN}
    if extra:
        h.update(extra)
    return h


def pad_post(path, payload):
    if not PAD:
        raise RuntimeError(PAD_HINT)
    req = urllib.request.Request(
        PAD + path, data=json.dumps(payload).encode(),
        headers=_pad_headers({"Content-Type": "application/json"}), method="POST")
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return json.loads(r.read().decode("utf-8"))


def pad_get(path):
    if not PAD:
        raise RuntimeError(PAD_HINT)
    req = urllib.request.Request(PAD + path, headers=_pad_headers())
    with urllib.request.urlopen(req, timeout=TIMEOUT) as r:
        return json.loads(r.read().decode("utf-8"))


TOOLS = [
    {
        "name": "get_screen",
        "description": (
            "Capture what is currently on the console/game screen as an image. "
            "Use this to see the live game state - menus, HUD, dialogue, what the "
            "player is doing right now."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "width": {
                    "type": "integer",
                    "description": "Width in pixels to scale to (default 1280). Smaller is cheaper; "
                                   "use 1920 only when fine detail like small text matters.",
                    "minimum": 160, "maximum": 3840,
                },
                "quality": {
                    "type": "integer",
                    "description": "JPEG quality 20-100 (default 80).",
                    "minimum": 20, "maximum": 100,
                },
            },
        },
    },
    {
        "name": "get_screen_burst",
        "description": (
            "Capture several frames in sequence to show MOTION - what is moving, "
            "which way, and what just happened. A single frame cannot answer that. "
            "Use for gameplay analysis, reaction timing, or teaching from demonstration."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "count": {"type": "integer", "description": "Frames to capture (2-8, default 4).",
                          "minimum": 2, "maximum": 8},
                "interval_ms": {"type": "integer",
                                "description": "Delay between frames in ms (50-2000, default 300).",
                                "minimum": 50, "maximum": 2000},
                "width": {"type": "integer", "minimum": 160, "maximum": 3840,
                          "description": "Width per frame (default 960 - bursts are several images)."},
            },
        },
    },
    {
        "name": "get_status",
        "description": "Whether capture is live, plus resolution, framerate, device and recording state. "
                       "Check this first if get_screen fails.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "press_button",
        "description": (
            "Press a controller button on the Steam Deck. Buttons: a, b, x, y, lb, rb, "
            "start, back, guide, l3, r3. Use get_screen first to see the state, then press, "
            "then get_screen again to see the result."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "button": {"type": "string", "description": "a|b|x|y|lb|rb|start|back|guide|l3|r3"},
                "ms": {"type": "integer", "description": "How long to hold, in ms (default 80). "
                                                         "Use longer for charged or hold-to-act inputs.",
                       "minimum": 10, "maximum": 5000},
            },
            "required": ["button"],
        },
    },
    {
        "name": "move_stick",
        "description": (
            "Move an analog stick or press the d-pad, then optionally return it to centre. "
            "Sticks take x/y from -1..1 (x: -1 left, +1 right; y: -1 up, +1 down). "
            "Triggers take 0..1. For the d-pad use direction instead."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "stick": {"type": "string", "description": "left|right|lt|rt"},
                "x": {"type": "number", "minimum": -1, "maximum": 1},
                "y": {"type": "number", "minimum": -1, "maximum": 1},
                "value": {"type": "number", "minimum": 0, "maximum": 1,
                          "description": "Trigger amount when stick is lt/rt."},
                "direction": {"type": "string", "description": "d-pad: up|down|left|right"},
                "ms": {"type": "integer", "minimum": 0, "maximum": 10000,
                       "description": "Hold this long then recentre. 0 (default) leaves it held - "
                                      "remember to recentre or the character keeps walking."},
            },
        },
    },
    {
        "name": "input_sequence",
        "description": (
            "Run several inputs in order, with waits - for combos, menu navigation, or a "
            "timed manoeuvre. Each step is {type: press|hold|axis|wait, ...}. This is far more "
            "reliable than separate calls when timing matters, because there is no round-trip "
            "between steps."
        ),
        "inputSchema": {
            "type": "object",
            "properties": {
                "steps": {
                    "type": "array",
                    "description": 'e.g. [{"type":"press","button":"a"},{"type":"wait","ms":200},'
                                   '{"type":"axis","axis":"lx","value":-1},{"type":"wait","ms":500},'
                                   '{"type":"axis","axis":"lx","value":0}]',
                    "items": {"type": "object"},
                },
            },
            "required": ["steps"],
        },
    },
    {
        "name": "release_all",
        "description": "Release every button and recentre every stick. Use after an error, or "
                       "whenever inputs might be stuck - a jammed stick keeps the character moving.",
        "inputSchema": {"type": "object", "properties": {}},
    },
    {
        "name": "controller_status",
        "description": "Whether the virtual gamepad is reachable, and which buttons/axes are "
                       "currently held. Check this if inputs seem to do nothing.",
        "inputSchema": {"type": "object", "properties": {}},
    },
]


def call_tool(name, args):
    """Return a list of MCP content blocks."""
    if name == "get_status":
        s = fetch_status()
        return [{"type": "text", "text": json.dumps(s, indent=2)}]

    if name == "get_screen":
        data = fetch_frame(int(args.get("width", 1280)), int(args.get("quality", 80)))
        return [{"type": "image", "data": data, "mimeType": "image/jpeg"}]

    if name == "get_screen_burst":
        count = max(2, min(8, int(args.get("count", 4))))
        gap = max(50, min(2000, int(args.get("interval_ms", 300)))) / 1000.0
        width = int(args.get("width", 960))
        out = []
        for i in range(count):
            if i:
                time.sleep(gap)
            # Label each frame: without an ordering marker a model has no way to
            # tell which way time runs through the images.
            out.append({"type": "text",
                        "text": "frame %d of %d (+%dms)" % (i + 1, count, int(i * gap * 1000))})
            out.append({"type": "image", "data": fetch_frame(width, 75), "mimeType": "image/jpeg"})
        return out

    if name == "press_button":
        pad_post("/press", {"button": args["button"], "ms": int(args.get("ms", 80))})
        return [{"type": "text", "text": "pressed %s" % args["button"]}]

    if name == "move_stick":
        if args.get("direction"):
            # A d-pad press is a hat axis that must be released, or the menu
            # keeps scrolling forever.
            pad_post("/sequence", {"steps": [
                {"type": "axis", "axis": args["direction"], "value": 1},
                {"type": "wait", "ms": int(args.get("ms", 80)) or 80},
                {"type": "axis", "axis": "hatx", "value": 0},
                {"type": "axis", "axis": "haty", "value": 0},
            ]})
            return [{"type": "text", "text": "d-pad %s" % args["direction"]}]

        stick = (args.get("stick") or "left").lower()
        hold = int(args.get("ms", 0))
        if stick in ("lt", "rt"):
            steps = [{"type": "axis", "axis": stick, "value": float(args.get("value", 1))}]
            if hold:
                steps += [{"type": "wait", "ms": hold}, {"type": "axis", "axis": stick, "value": 0}]
            pad_post("/sequence", {"steps": steps})
            return [{"type": "text", "text": "%s -> %s" % (stick, args.get("value", 1))}]

        ax, ay = ("lx", "ly") if stick == "left" else ("rx", "ry")
        x, y = float(args.get("x", 0)), float(args.get("y", 0))
        steps = [{"type": "axis", "axis": ax, "value": x},
                 {"type": "axis", "axis": ay, "value": y}]
        if hold:
            steps += [{"type": "wait", "ms": hold},
                      {"type": "axis", "axis": ax, "value": 0},
                      {"type": "axis", "axis": ay, "value": 0}]
        pad_post("/sequence", {"steps": steps})
        return [{"type": "text", "text": "%s stick -> (%.2f, %.2f)%s"
                                         % (stick, x, y, " then centred" if hold else " (still held)")}]

    if name == "input_sequence":
        pad_post("/sequence", {"steps": args.get("steps", [])})
        return [{"type": "text", "text": "ran %d steps" % len(args.get("steps", []))}]

    if name == "release_all":
        pad_post("/release_all", {})
        return [{"type": "text", "text": "all inputs released"}]

    if name == "controller_status":
        return [{"type": "text", "text": json.dumps(pad_get("/status"), indent=2)}]

    raise ValueError("unknown tool: %s" % name)


def respond(msg_id, result=None, error=None):
    m = {"jsonrpc": "2.0", "id": msg_id}
    if error is not None:
        m["error"] = error
    else:
        m["result"] = result
    sys.stdout.write(json.dumps(m) + "\n")
    sys.stdout.flush()


def main():
    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue
        try:
            msg = json.loads(line)
        except Exception:
            continue

        method = msg.get("method")
        msg_id = msg.get("id")

        if method == "initialize":
            respond(msg_id, {
                "protocolVersion": "2024-11-05",
                "capabilities": {"tools": {}},
                "serverInfo": {"name": "shadowcast", "version": "1.0.0"},
            })
        elif method == "tools/list":
            respond(msg_id, {"tools": TOOLS})
        elif method == "tools/call":
            params = msg.get("params") or {}
            try:
                content = call_tool(params.get("name"), params.get("arguments") or {})
                respond(msg_id, {"content": content})
            except urllib.error.URLError:
                respond(msg_id, {"content": [{"type": "text", "text": HINT}], "isError": True})
            except Exception as e:
                respond(msg_id, {"content": [{"type": "text", "text": "%s: %s" % (type(e).__name__, e)}],
                                 "isError": True})
        elif msg_id is not None:
            respond(msg_id, error={"code": -32601, "message": "method not found: %s" % method})
        # notifications (no id) need no reply


if __name__ == "__main__":
    import os
    API = os.environ.get("SHADOWCAST_API", API).rstrip("/")
    PAD = os.environ.get("SHADOWCAST_PAD", PAD).rstrip("/")
    PAD_TOKEN = os.environ.get("SHADOWCAST_PAD_TOKEN", PAD_TOKEN)
    main()
