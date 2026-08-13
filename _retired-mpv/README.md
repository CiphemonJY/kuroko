# Retired: the mpv/ffmpeg viewer

Superseded 2026-08-06 by `../mf-viewer.ps1`.

**Why:** this viewer captures through ffmpeg's **DirectShow** input. DirectShow
reaches the ShadowCast (a modern UVC device) through a legacy compatibility
layer that buffers *before any timestamp exists to read* — so it was invisible
to `../measure-latency.ps1`, which measured our path at a clean 80ms while the
picture still visibly trailed. The replacement captures via **MediaFoundation**
(the browser's getUserMedia stack, which is what Genki Arcade used, being
Electron/Chromium) and is noticeably faster on the same hardware.

Everything else here was correct and stays worth reading — the config comments
record five failed latency experiments and why each broke playback.

**To run it again:** it expects the mpv config beside it, so pass the real one:

    powershell -File "_retired-mpv\viewer.ps1"   # will NOT find ..\mpv

Either copy `../mpv/` in next to it, or edit `$cfgDir` in `viewer.ps1` to point
at `..\mpv`. `../measure-latency.ps1` still uses `../mpv/` and is unaffected.

`viewer.ps1.bak` is the original ffplay version from before the mpv migration.
