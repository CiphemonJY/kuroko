# Council slate — Windows Systems / Interop lens

**Reviewer:** Windows systems & interop expert (P/Invoke, window/DWM, process & thread lifetime, resource leaks, WebView2 hosting, device-loss robustness).
**Date:** 2026-08-07 · **Mode:** READ-ONLY. Nothing was modified, killed, or restarted.
**Live system state at review time (verified this session, not recalled):**

- `ShadowCast.exe` pid **36192**, started 02:54:31, single instance, `Listen` on `127.0.0.1:8791` (+15 sockets in `TIME_WAIT`).
- It owns exactly **one** `msedgewebview2` browser group (pid 18664). The other four browser groups on the box belong to `bambu-studio.exe` (pids 12236, 33744), `SearchHost.exe` (16540) and `KinderPlan.exe` (18516). **No orphaned WebView2 group from any prior ShadowCast run.**
- Handle count 573 @ 03:03:33 → 564 @ 03:07:06, threads 15 → 16. No acute handle/thread leak at idle.
- Audio endpoint `Digital Audio Interface (2- ShadowCast 2)` present, status OK.
- `settings.json` on disk: `{"saveDir":"C:\\Users\\james\\Videos\\ShadowCast","volume":0.9,"audioDelayMs":0,"audioFix":"off"}` — **no `audioEngine` key**, although `Program.cs:697` always writes one. The file therefore predates the current build.

> Note for the certified-negatives ledger: the "do not blanket-kill `msedgewebview2.exe`, 6 belong to SearchHost" entry is now **understated** — on this box the foreign owners are SearchHost **plus bambu-studio (two separate groups) plus KinderPlan**. A blanket kill would take down two of James's other running apps.

---

## 1. Lens review

### SOUND — do not touch

**S1. Every P/Invoke signature and struct layout checks out.** I walked all of them against the SDK headers:

- `DwmSetWindowAttribute` / `DwmExtendFrameIntoClientArea` (`Program.cs:15-19`); `MARGINS{L,R,T,B}` (`:43`) matches `cxLeftWidth,cxRightWidth,cyTopHeight,cyBottomHeight` in that exact order.
- `ReleaseCapture`, `SendMessage`, `SetWindowPos` (`:21-28`) — correct; `bool` returns marshal as 4-byte `BOOL` by default, which is right for all three.
- `CreateJobObject` with `CharSet.Unicode` → `CreateJobObjectW` (`:75-76`); `SetInformationJobObject`, `AssignProcessToJobObject` (`:78-80`) — correct.
- `JOBOBJECT_BASIC_LIMIT_INFORMATION` (`:83-91`): `long,long,uint,UIntPtr,UIntPtr,uint,UIntPtr,uint,uint` — exact match for `LARGE_INTEGER ×2, DWORD, SIZE_T ×2, DWORD, ULONG_PTR, DWORD ×2`. `IO_COUNTERS` 6×`ulong` (`:93-94`) and `JOBOBJECT_EXTENDED_LIMIT_INFORMATION`'s 4 trailing `SIZE_T` (`:96-101`) are correct. Class id 9 = `JobObjectExtendedLimitInformation` (`:103`), flag `0x2000` = `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`:104`). Correct.
- `NCCALCSIZE_PARAMS` (`:68-72`) matches `RECT rgrc[3]; PWINDOWPOS lppos;` under `LayoutKind.Sequential`; the struct is blittable so `StructureToPtr(..., fDeleteOld: false)` at `:283` is safe.

**No wrong signature and no wrong struct layout found. Nothing in this file is silently corrupting memory.**

**S2. AudioSession.cs COM vtable ordering is correct in every interface.** This was the highest-risk file and it survives a slot-by-slot audit:

- `IMMDeviceEnumerator` (`AudioSession.cs:26-31`): `EnumAudioEndpoints` slot 0, `GetDefaultAudioEndpoint` slot 1. ✔
- `IMMDevice` (`:35-40`): `Activate` slot 0. ✔
- `IAudioSessionManager2` (`:44-50`): correctly carries the **base** `IAudioSessionManager`'s two methods (`GetAudioSessionControl`, `GetSimpleAudioVolume`) before `GetSessionEnumerator`. This is the single most commonly-botched vtable in Core Audio interop and it is right here. ✔
- `IAudioSessionEnumerator` (`:54-58`): `GetCount`, `GetSession`. ✔
- `IAudioSessionControl2` (`:62-80`): all **nine** `IAudioSessionControl` slots in order (`GetState … UnregisterAudioSessionNotification`) then the five v2 slots (`GetSessionIdentifier … SetDuckingPreference`). ✔
- `ISimpleAudioVolume` (`:84-90`): `SetMasterVolume, GetMasterVolume, SetMute, GetMute`. ✔
- `ctl as ISimpleAudioVolume` (`:113`) is a legitimate `QueryInterface` — the session-control object does expose `ISimpleAudioVolume`. The comment at `:113` is accurate.

The header comment at `:15-17` ("declared with every method in vtable order … because COM dispatches by slot") is not just aspirational; it was actually followed. Leave this file's *shape* alone. (Its *lifetime* handling is a separate matter — see W12.)

**S3. `SetWindowPos(HWND_TOPMOST/NOTOPMOST)` instead of `Form.TopMost`** (`Program.cs:30-40`, used at `:762`) — correct, and the comment correctly names the handle-recreation hazard.

**S4. Path traversal is closed in the UI server.** `FrameServer.cs:84` reduces the request path with `Path.GetFileName` before `Path.Combine(_uiRoot, name)`, and the raw path is never URL-decoded (only query *values* are, `:148`), so `%2e%2e%2f` stays a literal filename. Backslash segments also collapse to a bare name. No escape from `_uiRoot`. Bind is `IPAddress.Loopback` (`:45`), never `Any`.

**S5. The three "done in the last hour" items audit clean.**
- `_web.DefaultBackgroundColor = Black` at `Program.cs:226`, set **before** `EnsureCoreWebView2Async` (`:506`) — supported ordering; the WinForms wrapper defers it onto the controller. Correct.
- `PermissionRequested` (`:512-521`): `Uri.TryCreate` + `u.IsLoopback && u.Port == ApiPort`, no `Deferral` on a synchronous handler, and non-matching origins fall through to `State.Default` (which prompts). Tight and correct.
- `_statsWired` (`:887`, `:935-942`) — the guard **does** hold across engine switches (one bool, set once, never reset), and this is **confirmed on the live log, not by inspection**: `shadowcast.log` prints every stats line *twice* from 02:34:37 through 02:54:07 (old binary), and exactly *once* from the 02:54:33 process start onward. Fix verified on the live system.

**S6. Answering the open question: yes, `_audioStats` is stopped on the ffplay and browser paths.** `StopAudio()` stops it (`:1121`), the engine-switch handler calls `StopAudio()` before `StartAudio()` (`:811-812`), and neither `StartAudio`'s ffplay branch nor the browser path restarts it. The tick body is additionally guarded by `_native is { Running: true }` (`:940`). It is never `Dispose()`d, but it is a process-lifetime object — not worth a change.

**S7. `ExtractWeb`'s content-compare-before-write** (`:1080-1081`) is a genuinely good AV-lock mitigation. **`FindTool`'s `WinGet\Links` exclusion** (`:1129-1156`) correctly avoids the app-execution-alias reparse point that breaks `UseShellExecute=false` + redirected streams. **`Native.BeginDrag`** (`:130-134`) with `lParam=0` is the standard trick and is fine — `DefWindowProc`'s move loop reads `GetMessagePos` itself.

**S8. WebView2 process lifetime is clean** — see the live evidence above. No teardown work is needed here.

---

### WEAK — the openings

**W1. `ToggleFullscreen` is inverted *and* recreates the window handle.**
`FormBorderStyle` is set to `None` in the constructor (`:217`) and changed nowhere else. So the guard at `:618` (`if (FormBorderStyle != FormBorderStyle.None)`) is **false on the first press**, and F11 takes the `else` branch (`:626-628`): `FormBorderStyle = Sizable; WindowState = _prevState` — a normal window **with a native Windows title bar**, i.e. the exact opposite of fullscreen. `Escape` (`:610`) is guarded on `FormBorderStyle == None`, which is the *normal* state, so ESC does the same thing.
Worse: assigning `FormBorderStyle` mutates the `CreateParams` style bits, which forces WinForms to **recreate the window handle** — precisely the hazard documented at `:30-33` and `:759-761` for `TopMost`. WebView2 re-attaches and loses mouse tracking, so the hover menu dies. The `TopMost` fix closed one handle-recreation path; this is a second one it never covered.
Mitigating: with the WebView2 focused (`FocusWeb`, `:294-300`) and `AreBrowserAcceleratorKeysEnabled = false` (`:557`), `Form.KeyDown`/`KeyPreview` generally does not see keys routed to the hosted Chromium child HWND — so it fires only when focus sits on the form itself. Latent, not theoretical.

**W2. Two disjoint fullscreen state machines that never observe each other.**
`SetFullscreen` (`:392-439`) owns `_fullscreen`/`_restoreState`/`_restoreBounds` and is driven **only** by `ContainsFullScreenElementChanged` (`:553-554`). `ToggleFullscreen` (`:616-630`) owns `_prevState`/`FormBorderStyle`. Neither reads the other's state.
Related: `WndProc` **skips the `WM_NCCALCSIZE` handler entirely when `_fullscreen`** (`:276`). With `WS_THICKFRAME` still in `CreateParams` (`:263`), `DefWindowProc` then computes a real sizing-border non-client frame, so in HTML-fullscreen the client area (and the WebView2) is inset from the monitor edge by the frame width. That is a strong candidate for the residual "strange border" the comment at `:386-391` is still chasing.

**W3. The audio-delay/buffer slider is dead on the native engine.**
The `audio-delay` message sets `_audioDelayMs` (`:829`) and restarts audio on the stated grounds that "For NATIVE this value is the buffer depth, so it must restart too" (`:832-834`). But `StartAudio` constructs `new NativeAudio(AudioDelayMs)` at `:923` — `AudioDelayMs` is the **CLI-argument property** (`:466-475`), not the live/persisted `_audioDelayMs`. `:231` (`if (_audioDelayMs < 0) _audioDelayMs = AudioDelayMs;`) shows the intended relationship. Net effect: moving the slider costs an audible audio restart and changes nothing. Log corroborates: every native start reads `(target 120ms)` — the clamp default for 0 — with `audioDelayMs: 0` persisted.

**W4. Unhandled-exception surface is wide open and silent.**
`Load += async (_, _) => { ApplyGlass(); await InitAsync(); };` (`:248`) is `async void` with **no try/catch** around `InitAsync`. There is no `Application.SetUnhandledExceptionMode`, no `Application.ThreadException` handler and no `AppDomain.UnhandledException` handler anywhere in the binary. If `CoreWebView2Environment.CreateAsync` (`:505`) or `EnsureCoreWebView2Async` (`:506`) throws — evergreen runtime mid-update, user-data folder locked, GPU sandbox failure — the process dies with a WinForms crash dialog and **zero log lines**. That is exactly the silent-failure class the `Log()` comment at `:1160` says cost three builds to find.

**W5. No single-instance guard, and the port collision fails *open*.**
`new FrameServer(ApiPort, …)` throws if 8791 is already bound; the catch logs and **continues** (`:597-602`), and then `core.Navigate(_pageUrl)` (`:604`) navigates to `127.0.0.1:8791` — **the other instance's server**. The second viewer therefore renders a working-looking UI while fighting the first over the exclusive video device, and `McpServer`'s default `SHADOWCAST_API = http://127.0.0.1:8791` (`McpServer.cs:27`) silently proxies to instance #1 regardless of which window the user believes is live. Several `--mcp` instances coexisting is fine (they are pure HTTP clients); several *viewers* is not. Untriggered today (one instance confirmed), not fixed.

**W6. `stream.ReadTimeout = 5000` is a no-op.** `FrameServer.cs:68` sets it, but `NetworkStream.ReadTimeout` only applies to **synchronous** `Read`; the actual read is `ReadAsync` (`:132`) and ignores it. A client that connects and sends nothing parks a `Task` forever, and `AcceptLoop` (`:50-59`) spawns `Serve` tasks with no concurrency cap. Low severity on loopback, but the one place with an explicit timeout has none. `ReadRequestLineAsync` also assumes the whole request line arrives in a single segment (`:131-137`) — true in practice for loopback, not guaranteed.

**W7. No device-loss recovery anywhere — the stated hard requirement is unmet.**
`WasapiCapture.RecordingStopped` is **logged and dropped** (`NativeAudio.cs:97-100`). On unplug the capture client takes `AUDCLNT_E_DEVICE_INVALIDATED` and the capture thread ends, but `_output` is untouched — so `Running => _output is not null` (`:36`) keeps reporting **true**, `_audioStats` keeps emitting plausible-looking lines, and audio is permanently dead until the user manually switches engines. There is **no `IMMNotificationClient`, no `WM_DEVICECHANGE` case in `WndProc`** (`Program.cs:274-291`), and `_audioFallback` (`:724`, `:234-238`) is a one-shot that stops itself on the first tick.
Compounding: the fallback's own condition is `_audio is null` (`:237`) — `_audio` is the **ffplay** handle, always null on the native path, and never nulled after `Kill()` (`:1124`) on the ffplay path either. So the test is wrong in both directions.
This is also the failure mode that lies in the log, which makes it doubly expensive to diagnose later.

**W8. Settings: torn write, silent total loss, and write amplification.**
`SaveSettings` does a bare `File.WriteAllText` over the live file (`:696`) — truncate-then-write, so a crash or power loss mid-write leaves a truncated file. `LoadSettings` wraps the entire parse in `catch { }` with **no log at all** (`:688`), so a corrupt file silently reverts `saveDir`, `volume`, `audioDelayMs`, `audioFix` and `audioEngine` to defaults with zero evidence — note the contrast with `SaveSettings`, which *does* log (`:699`). And `SaveSettings` is called on **every** `volume` web message (`:843-845`), i.e. once per slider sample, so the truncation window is open continuously while the user drags the volume.

**W9. `KillOrphanedAudio` does not do what its own comment claims.** The comment (`:635-641`) states it "Matches on ffplay processes holding OUR capture device, so nothing else is touched." The code (`:646-649`) kills **every** `ffplay.exe` on the machine. Same class of mistake as the certified "do not blanket-kill `msedgewebview2.exe`" rule, just quieter — and the comment actively hides it from the next reader. (The `Process` objects are also never disposed; negligible, startup only.)

**W10. Leak-on-throw in `StartAudio`'s native branch.** `_native = new NativeAudio(...)` at `:923`. If `Start()` throws **after** `StartRecording()`/`Play()` (`NativeAudio.cs:115-116`) — e.g. in the `new System.Windows.Forms.Timer` at `:121` — the catch at `Program.cs:949-953` sets `_native = null` **without `Dispose()`**, orphaning a live `WasapiCapture` + `WasapiOut` with no reference anywhere. Audio keeps running, unstoppable, and the next `StartAudio` opens a second capture on top of it. Low probability, very nasty consequence.

**W11. Log has no rotation and is not multi-writer safe.** `File.AppendAllText` (`:1168`) opens with `FileShare.Read`, so a second ShadowCast process collides and the entry is silently dropped by the `catch { }` (`:1171`) — losing log lines in exactly the multi-instance scenario you would most want them (W5). Unbounded growth is ~2 lines/min of stats ≈ 350 KB/day; not a disk-fill risk on this box, but nothing trims it.

**W12. NAudio/COM RCW hygiene is loose everywhere (soft, GC-bounded).** `FindCapture` returns an `MMDevice` out of a `using` enumerator and disposes neither it nor the rest of `all` (`NativeAudio.cs:55-63`); `Start` never disposes `render` (`:73`); `DefaultRenderRate` never disposes its device (`Program.cs:458-460`); `AudioSession.FindSession` releases **nothing** — enumerator, device, manager, session enumerator and every non-matching session control (`AudioSession.cs:98-116`), and `ApplyVolume` can call it 11 times in 4.4 s (`Program.cs:709-720`). All finalizer-reclaimed rather than leaked forever. Low severity today only because the native path short-circuits at `:713` before ever reaching `FindSession`.

**W13. Notes, not defects.** `Connection: close` on every response (`FrameServer.cs:163`) means one socket per request — 15 in `TIME_WAIT` right now against 1 `Listen`; fine at UI-poll rates, watch it under an MCP burst loop. `ApiStatus` blocks on `tcs.Task.Wait(4s)` (`Program.cs:377`) — safe today because it is only ever reached from a `FrameServer` threadpool task, but it is a UI-thread deadlock waiting for its first careless caller. `AssignProcessToJobObject`'s return value is discarded (`:1042`), so the orphan protection could be silently off. Mica/`DwmExtendFrameIntoClientArea` (`:315-319`) is now entirely hidden behind the opaque-black WebView2 (`:226`) except at the rounded corners — dead weight, harmless.

---

## 2. Leak & lifetime table

| Resource | Acquired | Released | Verdict |
|---|---|---|---|
| Job object handle `_job` | `Program.cs:633` → `:112-127` | never (intentional — `CloseHandle` is what kills the children) | **OK by design**; process exit closes it |
| `AllocHGlobal` for job info | `:119` | `:125` `finally { FreeHGlobal }` | **OK** |
| ffplay `Process _audio` | `:1035` | `Kill()` at `:1124`; **never `Dispose()`, never nulled** | **leaks** (GC-bounded); the stale non-null also breaks the `_audio is null` test at `:237` |
| ffplay job assignment | `:1042` | with the job | **OK**, but return value discarded → silent failure possible |
| ffplay stdout/stderr async readers | `:1044-1047` | on child exit | **OK** |
| `FrameServer _api` | `:599` | `:249` → `FrameServer.cs:171` cancel + `Stop()` | **OK**; `_cts` itself never disposed and in-flight `Serve` tasks not drained (minor) |
| `TcpClient` / `NetworkStream` per request | `FrameServer.cs:55, 67` | `using` at `:65, :67` | **OK** |
| `NativeAudio _native` | `Program.cs:923` | `:1122` `_native?.Dispose()` | **OK on the normal path**; **leaks a live `WasapiCapture`+`WasapiOut`** if `Start()` throws after `NativeAudio.cs:115-116` (`Program.cs:949-953` nulls without Dispose) |
| `WasapiCapture` / `WasapiOut` | `NativeAudio.cs:75, :112` | `Stop()` `:152-156` (both `Dispose`s join their threads) | **OK** — the `_buffer = null` race at `:156` vs the `DataAvailable` closure at `:96` is closed by that join |
| `MMDevice dev` (capture endpoint) | `NativeAudio.cs:68` | never | **leaks** 1 RCW per `Start` (GC-bounded) |
| `MMDevice render` | `NativeAudio.cs:73` | never | **leaks** 1 RCW per `Start` (GC-bounded) |
| `MMDevice[]` from `EnumerateAudioEndPoints` | `NativeAudio.cs:58` | never | **leaks** N RCWs per `Start` (GC-bounded) |
| `MMDevice` in `DefaultRenderRate` | `Program.cs:458-460` | enumerator via `using`; device never | **leaks** 1 RCW per ffplay start (GC-bounded) |
| COM chain in `AudioSession.FindSession` | `AudioSession.cs:98-113` | never (`ReleaseComObject` absent) | **unclear / soft leak** — GC-bounded, but 1 `MMDeviceEnumerator` per call × up to 11 calls per volume change |
| `ISimpleAudioVolume` returned | `AudioSession.cs:113` | never | **soft leak**, same |
| `Timer _audioStats` | `Program.cs:886` | `Stop()` `:1121`; never `Dispose()` | **OK-ish** (process-lifetime) |
| `Timer _audioFallback` | `:724` | `Stop()` `:249`, `:861`; never `Dispose()` | **OK-ish** (process-lifetime) |
| `Timer _trim` (NativeAudio) | `NativeAudio.cs:121` | `:152` `Stop()` + `Dispose()` | **OK** |
| Navigation-retry `Timer` | `Program.cs:577` | `:580` `Stop()` + `Dispose()` inside Tick | **OK** |
| `ApplyVolume` retry `Timer` | `:717` | `:718` `Stop()` + `Dispose()` inside Tick | **OK** |
| `_audioStats.Tick` handler | `:938` | never removed | **OK** — bounded to exactly one by `_statsWired`; **verified against the live log** |
| `Icon` from manifest stream | `:206` | never | negligible, one-shot |
| `Process[]` from `GetProcessesByName` | `:646` | never disposed | negligible, startup only |
| WebView2 browser process group | `:506` | control disposal at process exit | **OK** — live check: 1 group owned by pid 36192, 0 orphans from prior runs |

Aggregate: no unbounded native-handle leak. Handle count on the running instance went 573 → 564 over 3.5 min at idle. Everything above marked "leaks" is GC-bounded and only matters because `StartAudio` runs on **every** engine switch, fix change and delay change (`:811-834`).

---

## 3. Top 5 proposals (none of these are on the already-done list)

### P1 — Device-loss detection + bounded auto-recovery for the native audio engine
**Mechanism.** Make `NativeAudio.Running` reflect reality instead of `_output is not null` (`NativeAudio.cs:36`); set a `_failed` flag from the `RecordingStopped` handler (`:97-100`). Add a 3 s watchdog (or reuse the existing `_audioStats` tick, `Program.cs:938-941`) that, on `_failed`, calls `StopAudio(); StartAudio();` with exponential backoff (3/6/12 s, cap 30 s, give up after ~10 tries and `Toast`). Add a `WM_DEVICECHANGE` (`0x0219`) case to `WndProc` (`:274-291`) so a replug triggers an immediate retry instead of waiting out the backoff. Fix the `_audioFallback` predicate at `:237` to test the *active engine*, not `_audio`.
**Failure prevented.** Permanent silent audio death after a dongle unplug/replug — plus the log actively lying about it (`Running` true, normal-looking stats lines) which makes it expensive to diagnose after the fact.
**IMPACT 5 · CONFIDENCE 5 · EFFORT M**
**Cheap falsification test (~2 min, no code):** with the app running, disable then re-enable *Digital Audio Interface (2- ShadowCast 2)* in Device Manager. Tail `%LOCALAPPDATA%\ShadowCast\shadowcast.log`. Predicted today: one `native audio capture stopped: …` line, then normal-looking 30 s stats lines continuing forever with a frozen buffer figure, and audio never returns. If audio comes back on its own, this proposal is wrong.

### P2 — Fix the fullscreen path; delete the handle-recreating branch
**Mechanism.** Replace the `KeyDown` body (`Program.cs:607-611`) with `F11 → SetFullscreen(!_fullscreen)` and `Esc → if (_fullscreen) SetFullscreen(false)`. Delete `ToggleFullscreen` and `_prevState` (`:614-630`) — one state machine, not two. Drop `&& !_fullscreen` from the `WM_NCCALCSIZE` guard (`:276`) so the non-client frame stays swallowed at monitor size.
**Failure prevented.** (a) F11/ESC producing a native title bar instead of fullscreen; (b) a `FormBorderStyle` assignment **recreating the window handle** and re-attaching WebView2 — the exact failure the `TopMost` certified negative documents, reached through a path that fix never covered; (c) the residual inset frame in HTML-fullscreen (W2).
**IMPACT 4 · CONFIDENCE 4 · EFFORT S**
**Cheap falsification test (~15 s):** click the video area, then press ESC. Predicted: a native Windows title bar appears and the borderless chrome is gone; afterwards the hover menu no longer appears (handle recreation). If nothing happens at all, the Form never receives the key — downgrade impact to 2 but keep the fix, since focus can legitimately land on the form.

### P3 — Guard the process: single-instance mutex, fail-closed port bind, and a global exception handler that logs
**Mechanism.** (a) In `Main` after the `--mcp`/`--loopback` branches (`:151-162`): `new Mutex(true, @"Local\ShadowCast.Viewer", out var isNew)`; if not new, log and exit (or foreground the existing window). (b) In `InitAsync`, make `FrameServer` construction fatal-with-a-visible-message instead of logging and navigating on to a *foreign* server (`:597-604`). (c) `Application.SetUnhandledExceptionMode(CatchException)` + `Application.ThreadException` + `AppDomain.CurrentDomain.UnhandledException`, all routed to `Log`; and wrap the `Load` async-void body (`:248`) in try/catch → `Log` + `Toast`.
**Failure prevented.** Two viewers fighting the exclusive device while the MCP server silently proxies to whichever one won the port; and a WebView2 init failure killing the app with **no log line at all**.
**IMPACT 4 · CONFIDENCE 5 · EFFORT S**
**Cheap falsification test (~2 min):** launch a second `ShadowCast.exe` while the first runs. Predicted: it starts, logs `local server failed to start: …`, and still shows a working-looking UI — because it is being served by instance #1. Confirm by watching instance #1's `TIME_WAIT`/`Listen` ownership (`Get-NetTCPConnection -LocalPort 8791`) stay on the *first* pid. Separately, temporarily point `%LOCALAPPDATA%\ShadowCast\WebView2` at an unwritable path and confirm the crash leaves zero new log lines.

### P4 — Atomic settings write, loud load failure, debounced volume save
**Mechanism.** `SaveSettings` (`:691-700`) writes `settings.json.tmp` then `File.Move(tmp, path, overwrite: true)` (or `File.Replace` keeping a `.bak`). `LoadSettings`'s `catch { }` (`:688`) becomes `catch (Exception ex) { Log($"settings unreadable, using defaults: {ex.Message}"); }` and renames the bad file to `settings.bad` so it is recoverable. The `volume` handler (`:839-846`) applies immediately but debounces the *save* behind a 750 ms one-shot timer.
**Failure prevented.** Losing the save folder / engine / delay / volume to a truncated file, with no diagnostic trail; and hundreds of full-file rewrites (each a truncate) per volume drag.
**IMPACT 3 · CONFIDENCE 5 · EFFORT S**
**Cheap falsification test (~1 min):** `Copy-Item settings.json settings.json.bak`, then `Set-Content settings.json '{"saveDir":'`, restart, open the menu. Predicted: the save folder silently reverts to `Videos\ShadowCast` and the log says nothing whatsoever. Restore from the `.bak` afterwards.

### P5 — Make the delay slider reach the native engine; close the two `_audio`/`_native` lifetime holes
**Mechanism.** `Program.cs:923` → `new NativeAudio(_audioDelayMs > 0 ? _audioDelayMs : AudioDelayMs)`. In the catch at `:949-953`, call `_native.Dispose()` before nulling. At `:1124`, `Dispose()` the ffplay `Process` and set `_audio = null`. Optionally `Marshal.FinalReleaseComObject` on `AudioSession.FindSession`'s exit paths (`AudioSession.cs:100-116`).
**Failure prevented.** A UI knob that costs an audible audio restart and changes nothing (W3); an orphaned, unstoppable `WasapiCapture`/`WasapiOut` on a `Start()` throw (W10); a stale `_audio` that permanently falsifies the fallback test at `:237`.
**IMPACT 3 · CONFIDENCE 5 · EFFORT S**
**Cheap falsification test (~30 s):** set `"audioDelayMs": 300` in `settings.json`, restart, read the next `native audio: … (target Nms)` line. Predicted: it still says `target 120ms` (the clamp default for 0), because only `-AudioDelayMs 300` on the command line can move it.

**Honourable mentions (below the cut, all EFFORT S):** replace the no-op `stream.ReadTimeout` with a `CancellationTokenSource(5s)` on `ReadAsync` and cap concurrent `Serve` tasks with a `SemaphoreSlim` (W6); log-file rotation at ~2 MB (W11); either implement the filter `KillOrphanedAudio`'s comment promises or correct the comment to say it kills every `ffplay.exe` (W9); log `AssignProcessToJobObject`'s return value (W13).

---

## 4. Ranking — impact × confidence ÷ effort (S=1, M=2, L=3)

| Rank | Proposal | I × C ÷ E | Score |
|---|---|---|---|
| 1 | **P3** — mutex + fail-closed port + global exception logging | 4 × 5 ÷ 1 | **20.0** |
| 2 | **P2** — fullscreen unification / kill the handle-recreation path | 4 × 4 ÷ 1 | **16.0** |
| 3 | **P4** — atomic settings write + loud load failure | 3 × 5 ÷ 1 | **15.0** |
| 3= | **P5** — delay slider + `_audio`/`_native` lifetime holes | 3 × 5 ÷ 1 | **15.0** |
| 5 | **P1** — device-loss detection + auto-recovery | 5 × 5 ÷ 2 | **12.5** |

**Recommended execution order differs from the arithmetic.** P1 ranks last only because it is the one M-effort item; it is the **only** proposal on this slate that addresses a stated hard requirement ("the app must survive the dongle being unplugged mid-session"), and its failure mode is the one that *lies in the log*. Do **P1 first**, then P3, then P2/P4/P5 as a single small batch — P3, P4 and P5 together are perhaps 60 lines and touch nothing the certified negatives protect.

**Nothing on this slate re-proposes a certified negative.** Specifically: no virtual-host mapping, no `HttpListener`, no `Form.TopMost`, no `MoveFocus`, no change to the job-object orphan fix, no blanket `msedgewebview2` kill, no bundling of ffmpeg/ffplay.
