# Kuroko

A low-latency HDMI capture viewer for Windows, with a machine-readable seam:
the running viewer exposes the live picture over a loopback HTTP API, so a
vision model can *watch* a console screen — and, paired with `deck/deckpad.py`,
*play* it.

Replaces the discontinued Genki Arcade app for a UVC capture dongle.

## Layout

| Path | What it is |
| --- | --- |
| `native/` | The shipped app (C# / .NET 9 WinForms). Borderless glass window hosting a WebView2 that captures via **MediaFoundation** (`getUserMedia`), plus in-process WASAPI audio through NAudio. Single-file self-contained publish. |
| `native/web/mf-viewer.html` | The capture page. Embedded into the exe as a resource and served from a loopback origin — `getUserMedia` needs a secure context, so `file://` will not do. No external scripts or styles. |
| `mcp/kuroko_mcp.py` | Standalone Python MCP server (stdio JSON-RPC, no third-party packages). Superseded by the in-exe server, kept as the interpreter-only route. |
| `deck/` | `deckpad.py` — a virtual Xbox 360 pad created through `/dev/uinput` **on a Steam Deck**, driven over HTTP. Plus `verify_pad.py` (reads the pad's own event device to prove real kernel events) and the `clickscan`/`dropscan` audio-fault detectors. |
| `mf-viewer.ps1`, `mf-viewer.html` | The browser-hosted viewer the native app grew out of. Still useful for isolating a problem to the app vs. the capture path. |
| `mpv/`, `_retired-mpv/` | The earlier mpv/ffplay viewer and its configs. Retired 2026-08-06 — the config comments record five failed latency experiments and why each broke playback. |
| `measure-latency.ps1` | Objective capture→screen latency measurement (correlates dongle frames against what is actually lit on the monitor). |
| `usb-watch.ps1` | Live USB arrival/removal watch — separates "dongle not enumerating" from a driver or software fault. |
| `council/slates/` | Expert-council reviews: security, legal/IP, monetization, open-source, product-market, audio DSP, Windows internals. |

## Build

```
dotnet publish native\Kuroko.csproj -c Release -o native\publish
```

Produces one self-contained `Kuroko.exe` (~109 MB, git-ignored). Needs the
.NET 9 SDK; NuGet pulls WebView2 and NAudio.

## The agent seam

The dongle is an **exclusive** device: while the viewer holds it, nothing else
can open it. So no separate tool can grab frames — the viewer's loopback API is
the only way to share the live picture.

`Kuroko.exe --mcp` runs an MCP server over stdio from the same exe (no
Python needed) that proxies to that API:

- `get_screen` — one frame as an image
- `get_screen_burst` — several frames over a span, for "what just happened"
- `get_status` — resolution, fps, capture state
- `press_button` / `move_stick` / `input_sequence` — gamepad input, forwarded to
  `deckpad` on the Deck

`/status` and `/frame` require a token that is regenerated every launch and
written to a file (not a command line — those are world-readable via WMI).

Controller input runs on the Deck rather than the PC because Windows cannot be
a Bluetooth HID peripheral and a USB-C host-to-host cable joins two hosts.
Linux can just create the gamepad locally, and it is lower latency than
Bluetooth.

Environment: `KUROKO_API` (default `http://127.0.0.1:8791`),
`KUROKO_PAD` (e.g. `http://steamdeck:8792`), `KUROKO_PAD_TOKEN`.

## The name

Renamed from ShadowCast on 2026-08-12. "ShadowCast" is Human Things/Genki's
hardware product name — the dongle this software drives — so using it as *this*
project's name was identical mark, identical goods, identical customers.
Referring to the device by its real name is fine; being named after it is not.

Two places still say ShadowCast, both deliberately:

- **Device-matching strings** (`NativeAudio.cs`, `Program.cs`, `mf-viewer.html`,
  `mf-viewer.ps1`, `measure-latency.ps1`) — that is how Windows enumerates the
  dongle. They are load-bearing; changing them means no capture device is found.
- **`council/` and `_retired-mpv/`** — dated records of a thing that was called
  ShadowCast. Rewriting them would falsify the record.

## Status

Working, in daily use. `council/slates/security.md` is a live audit of the
loopback and Deck surfaces — read it before exposing any of this beyond
localhost and a trusted tailnet.
