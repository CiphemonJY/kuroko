# Council slate — Real-time Audio / DSP lens

Reviewer: audio/DSP expert. Scope: audio correctness, control theory, DSP artifacts,
real-time scheduling of the audio path. Read-only; nothing was modified and the running
app was not touched.

Evidence base: `native/NativeAudio.cs`, `native/Program.cs`, `native/LoopbackRecorder.cs`,
`deck/dropscan.py`, `deck/clickscan.py`, `native/ShadowCast.csproj`,
`%LOCALAPPDATA%\ShadowCast\shadowcast.log` (lines 3–269), and the NAudio 2.2.1 package
metadata in `~/.nuget/packages/naudio.wasapi/2.2.1/lib/netstandard2.0/NAudio.Wasapi.xml`.

---

## 0. Calibration facts derived from the log (used throughout)

These are measured, not assumed. All from `shadowcast.log`.

**(F1) The render `Read()` chunk is 480 frames = 10 ms, so `DriftTrim.Read` runs ~100x/sec.**
Line 241/242 (02:49:37, duplicate-handler era, two `Stats()` calls microseconds apart):
`corr 50263 → 50264`, `trim -673 → -693ppm`, `buffer 92ms → 82ms`.
One `Read` call elapsed between the two lines: `corr` +1, `_trim` moved exactly one slew
step (`0.00002` = 20 ppm, `NativeAudio.cs:226`), and the buffer fell exactly 10 ms.
⇒ one `Read` consumes 10 ms. Same at 223/224 (105→95 ms, corr unchanged because
`trim` was `+0` and `corr` only counts `|trim|>100ppm`).

**(F2) The EMA time constant is therefore ~0.5 s**, as claimed:
`alpha 0.02 * 100 calls/s` → `tc = 0.5 s` (`NativeAudio.cs:222`). The claim is arithmetically
correct. Its *effect* is the problem (§1 W2).

**(F3) Capture delivers in ≥60 ms atomic bursts.**
Line 233/234 (02:47:37): `buffer 71ms` and `buffer 131ms` in two `Stats()` calls with
**identical** `corr 45594` — i.e. zero render `Read` calls between them, so ~zero elapsed
render time — yet the buffer gained 60 ms. That 60 ms arrived as ONE
`AddSamples` (`NativeAudio.cs:96`). This is a direct measurement of the burst size, not
an inference.

**(F4) `_trim` does NOT rest at 0.** Sampled non-zero values in the *current* build
(post-02:55 restart): `-1500, -20, -871` (lines 265, 268, 269). In the 02:40–02:54 build:
`-1158, -1500, -1500, -60, -80, -740, -260, -673, -693, -607, -627, -1190`.
`-1500ppm` is the hard clamp (`Math.Clamp(..., -0.0015, 0.0015)`, `NativeAudio.cs:225`).
The controller is repeatedly slammed against its clamp.

**(F5) The EMA change did not reduce ratio chatter.**
`corr` per 30 s, pre-EMA build (lines 5–43, 01:26–01:45): 880, 1641, 2154, 2118, 2261,
1554, 2027, 1365, 1072, 1165, 1153, 1236, 699, 1310, 1477, 907, 1382, 1184, 1057, 1006,
851, 1389, 1062, 1309, 1348, 1228, 755, 1288, 1063, 1334, 1364, 1507, 1569, 2995, 1467,
2003, 2039, 2338 → **mean ≈ 1400 / 30 s ≈ 47/s**.
`corr` per 30 s, post-EMA build (lines 265–269): 1962, 2174, 2338, 882 →
**mean ≈ 1840 / 30 s ≈ 61/s**.
Change (a) did not lower the chatter duty; if anything it rose. This is the single most
important adversarial finding and it falsifies the "trim now rests at exactly +0ppm" claim.

**(F6) The duplicate-stats-handler fix works.** Every log line 206–260 appears in pairs
(two `Stats()` per tick); every line from 265 on is single. The `_statsWired` guard
(`Program.cs:935`) is doing its job in the running build. Not a live bug.

---

## 1. Lens review — SOUND vs WEAK

### SOUND — do not touch

**S1. The no-conversion architecture and the hard bail.** `NativeAudio.cs:75–87`: capture
endpoint rate vs render mix rate compared, and `Start()` refuses rather than resample.
This is the right invariant and the right enforcement. Verified live: log line 5+
`in 48000Hz / out 48000Hz`.

**S2. The read-accounting fix (`tailFrames` subtraction).** `NativeAudio.cs:240–241`.
Verified exact by telescoping in §2. The historical `~4 ms/sec` drain cannot recur.

**S3. The `_pos + 2 < avail` guard.** `NativeAudio.cs:268`. Verified in §2 to be
simultaneously (a) in-bounds for the highest tap `i1+2`, and (b) never able to truncate an
output block. Both properties hold for any `ratio > 0`.

**S4. The `consumed - 1` history carry.** `NativeAudio.cs:291`. Verified in §2: the
`i0 = i1 > 0 ? i1-1 : 0` clamp (`NativeAudio.cs:272`) fires **exactly once per stream**
(first call only), not once per block. The stated worry — a block-rate clamp becoming a
tone — is genuinely eliminated. Good change.

**S5. Moving the starvation detector before the read.** `NativeAudio.cs:253–254`. Correct
diagnosis: with `ReadFully = true` (`NativeAudio.cs:93`) `BufferedWaveProvider.Read`
always returns `count`, so the zero-fill loop at `:304` truly is unreachable and the old
counter was structurally dead. Measuring `BufferedBytes/BlockAlign` before the read is the
only visible truth. Correct.

**S6. No steady-state allocation in the audio path.** `_in`/`_tail` pre-sized at
`NativeAudio.cs:205–206`; the only `new` in `Read` is the growth guard at `:244` and `:295`,
neither of which can fire once sized. Paired with
`GCLatencyMode.SustainedLowLatency` (`Program.cs:171`) and workstation+concurrent GC in the
csproj. Right instincts throughout.

**S7. Render is event-driven.** `new WasapiOut(render, Shared, true, 80)`
(`NativeAudio.cs:112`) — the `true` is `useEventSync`. Correct. (Capture is not; see W1.)

**S8. `VolumeSampleProvider` placed after `DriftTrim`** (`NativeAudio.cs:108`) is fine —
gain and interpolation are both linear and commute. The position is not the problem; the
missing ramp is (W7).

**S9. `LoopbackRecorder`'s reused scratch buffer** (`LoopbackRecorder.cs:41`) and the
clamped float→s16 conversion (`:55`). The instrument no longer manufactures its own
dropouts and cannot fake a click by wrapping. Sound.

### WEAK — the openings

**W1. Capture is POLLED with ~50 ms sleeps. This is the largest single defect.**
`NativeAudio.cs:75`: `new WasapiCapture(dev) { ShareMode = Shared }` uses the 1-argument
constructor. NAudio 2.2.1's own docs
(`NAudio.Wasapi.xml:3188`) define the parameter it defaults:
`useEventSync — "true if sync is done with event. false use sleep."` The 1-arg overload
passes `false` and a 100 ms `audioBufferMillisecondsLength`; `DoRecording` then
`Thread.Sleep(bufferMs/2)` ≈ 50 ms between packet drains.
Consequence measured directly at **F3**: 60 ms of audio lands in a single `AddSamples`.
That burst — not clock drift — is the entire ±25–30 ms depth swing the EMA was added to
paper over. You are filtering a disturbance you could have removed at source.
Downstream costs: (i) the controller's input is dominated by an artifact of the polling
period; (ii) the 120 ms target exists mostly to absorb this burst, so 60–80 ms of the
latency budget is polling slack; (iii) a scheduler delay on a *sleeping* thread has no
priority protection at all (see W4).

**W2. The controller is a deadband-proportional law on an integrator plant. It cannot rest,
so it limit-cycles by construction — and the EMA cannot fix that.**
`NativeAudio.cs:224–227`:
```
err  = (_depthAvg - 120) / 120
want = |err| < 0.06 ? 0 : clamp(err * 0.01, ±0.0015)
_trim += clamp(want - _trim, ±0.00002)
```
Arithmetic:
- Deadband = ±0.06 × 120 ms = **±7.2 ms**, i.e. `_depthAvg ∈ (112.8, 127.2) ⇒ want = 0`.
- The control law is **discontinuous at the deadband edge**: at `|err| = 0.06⁻` want is 0;
  at `0.06⁺` it is `0.06 × 0.01 = 600 ppm`. A 0.1 ms change of average depth commands a
  600 ppm step. This is a relay, not a proportional controller.
- Plant: buffer depth is the *integral* of rate mismatch. `trim = -1500 ppm` refills the
  buffer at only 1.5 ms/s, so crossing the 14.4 ms deadband takes ~10 s. Slewing 0 → 1500 ppm
  takes `0.0015 / 0.00002 = 75` calls = **0.75 s** (F1). Relay + integrator + lag = a
  guaranteed limit cycle with period on the order of tens of seconds.
- **Equilibrium is pinned to the deadband edge, not the target.** Under a persistent
  negative drift the loop can only push up when depth < 112.8 ms, so the resting depth is
  ~112.8 ms, never 120 ms. The log agrees: `avg` sits at 106–117 ms in almost every
  sample (lines 211–269), i.e. it lives *on the lower edge*, and `trim` is negative in
  every non-zero current-build sample (F4).
- Duty: `corr` counts calls with `|trim| > 100 ppm` (`NativeAudio.cs:228`). At 61/s of
  100 calls/s (F1, F5), the ratio is off-zero **~60 % of the time**, with excursions to the
  full ±1500 ppm clamp.

The EMA (change a) attacks the *noise* on the controller input. The limit cycle is caused
by the *structure* of the control law, so filtering the input cannot remove it — and F5
shows empirically that it didn't. Correct change, wrong target.

**W3. The `corr` vs `trim +0ppm` discrepancy is not a counter bug — it is instrumentation
aliasing, and it is hiding W2.** `corr` is a per-call counter over ~3,000 calls per 30 s
interval; `trim` in `Stats()` (`NativeAudio.cs:314`) is a **single instantaneous sample**.
With a ~60 % duty cycle you will read `+0` on roughly 40 % of snapshots by chance, and the
log duly shows both `+0` and `-1500` within the same minute (lines 265–269). Nothing is
wrong with the counter; what is wrong is that the headline number reported to the human is
the one statistic that cannot see the failure. `Stats()` should report min/max/mean of
`_trim` and of `depth` over the interval, not point samples. Note also that the printed
`buffer NNNms` is pure burst noise: 71 ms and 131 ms in the same second (F3).

**W4. Neither audio thread joins MMCSS. The suspicion is CONFIRMED statically.**
`strings` over `NAudio.Wasapi.dll` and `NAudio.Core.dll` (2.2.1) finds **no** reference to
`AvSetMmThreadCharacteristics`, `avrt`, `Mmcss`, or `"Pro Audio"` — NAudio never registers
either thread with the Multimedia Class Scheduler. So the render thread and the (sleeping,
W1) capture thread are ordinary managed threads competing with WebView2's GPU/compositor
processes at 1080p60, with no priority boost and no starvation protection. The 80 ms
device buffer (`NativeAudio.cs:112`) is the only margin, and `Program.cs:166` already
records a 66–99 ms measured gap that was attributed to GC — MMCSS absence is an equally
good candidate for that class of event and costs one P/Invoke to remove.

**W5. Catmull-Rom's fractional-offset-dependent HF response amplitude-modulates the
signal at `|trim| × 48000` Hz. Change (b) improved this ~2.4x but did not remove it.**
Kernel gain at fractional offset `f`, referenced to `p1`:
`H_f(ω) = c0·e^{jω} + c1 + c2·e^{-jω} + c3·e^{-2jω}` with the Catmull-Rom coefficients at
`NativeAudio.cs:277–279`. At `f = 0` the taps are `(0,1,0,0)` → `|H| = 1` at every
frequency (perfectly flat). At `f = 0.5` the taps are `(-1/16, 9/16, 9/16, -1/16)` →
`|H| = |1.125·cos(ω/2) − 0.125·cos(3ω/2)|`:

| freq | \|H\| at f=0 | \|H\| at f=0.5 | peak-to-peak AM |
|---|---|---|---|
| 5 kHz | 1.000 | 0.9959 (−0.04 dB) | 0.4 % |
| 10 kHz | 1.000 | 0.9404 (−0.53 dB) | 6 % |
| 12 kHz | 1.000 | 0.8839 (−1.07 dB) | 12 % |
| 15 kHz | 1.000 | 0.7476 (−2.52 dB) | 25 % |
| 20 kHz | 1.000 | 0.3796 (−8.4 dB) | 62 % |

`f` advances by `(ratio − 1) = _trim` per output sample, so it wraps 0→1 at
`|_trim| × 48000` Hz. At the `-1500 ppm` clamp that is **72 Hz**; at `-100 ppm`, 4.8 Hz.
So high-frequency content is amplitude-modulated at 6–25 % depth (10–15 kHz) at a rate
that **sweeps 0→72 Hz and back on every limit-cycle excursion** (W2). AM in the 5–70 Hz
band is heard as roughness/fizz, and a sweeping rate is heard as fluttering roughness.
For reference, linear interpolation gave `|H| = cos(ω/2)`: 0.793 at 10 kHz (−2.0 dB) and
0.707 at 12 kHz — so change (b) is directionally right and bought ~2.4x, but the residual
is not small and the limit cycle keeps it in the worst rate band.
Note this artifact **exists only while `trim ≠ 0`**; the deadband makes `trim = 0` common,
so the artifact is intermittent — which matches the historical "intermittent crackle/fizz"
reports better than any starvation hypothesis does.

**W6. `drops`/`pads` are near-useless, as suspected — and `starved` is contaminated by
startup so it is useless too.**
- `pads`: `ms < TargetMs * 0.25` = 30 ms (`NativeAudio.cs:143`), sampled at 1 Hz on the
  **UI thread**. Given F3 (60 ms bursts) and a ~10 ms render granularity, a 1 Hz sample of a
  quantity that moves 60 ms in one step is aliasing, not measurement. Logged value: `pads 4`
  then `pads 1` — both accrued at startup, then frozen forever.
- `drops`: `ms > TargetMs * 3` = 360 ms (`:135`), against a `BufferDuration` of
  `TargetMs * 6` = 720 ms with `DiscardOnBufferOverflow = true` (`:91–92`). Never fired
  (`drops 0` for the whole log). It is a tripwire set beyond where the fault would already
  be catastrophic.
- `starved 8 (150.0ms silence)` / `starved 1 (80.0ms silence)`: these are **all startup**.
  `_output.Play()` is called immediately after `_capture.StartRecording()`
  (`NativeAudio.cs:115–116`) with a completely empty buffer, so the first ~80 ms of every
  session is guaranteed silence + a starvation event. Both counters are frozen thereafter.
  A permanently-frozen counter whose value is startup noise cannot function as an alarm:
  a real mid-session starvation would have to raise `8` to `9` to be noticed.

**W7. Volume is applied as an un-ramped step, from another thread.**
`NativeAudio.cs:46` sets `_volumeNode.Volume` from the UI thread; NAudio's
`VolumeSampleProvider` is a bare per-sample multiply with no smoothing. Every slider
message (`Program.cs:839–846`) is therefore a gain discontinuity at an arbitrary sample —
a click whose amplitude is proportional to the instantaneous signal level and the step
size. A slider *drag* produces a burst of these (classic zipper noise). This is a real,
certain click source in a build whose click count is otherwise 0, and it fires exactly when
a user is fiddling — a strong match for "intermittent".

**W8. The `drops` path is an uncrossfaded splice executed on the UI thread.**
`NativeAudio.cs:137–141` reads and discards up to `(ms − Target)` ≈ 240 ms in one cut,
concurrently with the render thread's read. `BufferedWaveProvider`'s circular buffer is
internally locked so there is no corruption, but the result is a hard splice with no
crossfade — a guaranteed loud click, and the carried `_tail` history (S4) will then be
pre-splice audio spliced onto post-splice audio, so Catmull-Rom smears the discontinuity
over 2 samples rather than removing it. It has never fired (`drops 0`), so this is latent,
not live — but it is the single worst-sounding line of code in the file.

**W9. Only sample RATE is validated, not channel count or bit depth.**
`NativeAudio.cs:76–87` compares `SampleRate` only. If the default render endpoint is ever
not 2-channel float (HDMI receiver, 5.1 virtual device, a Bluetooth headset at 1ch), then
`_output.Init(_volumeNode)` (`:113`) will silently insert NAudio's resampler/DMO to
reconcile formats — reintroducing exactly the conversion stage the whole design exists to
eliminate, with no log line to say so. The invariant is enforced on one axis out of three.

**W10. No `PlaybackStopped` handler on `_output`.** `RecordingStopped` is wired
(`NativeAudio.cs:97–100`) but the render side is not. A default-device change, a driver
reset, or a disconnected endpoint stops playback silently and the app keeps reporting
`Running => _output is not null` (`:36`). Asymmetric error handling.

**W11. The EMA is a time-variant filter.** `alpha = 0.02` is applied per *call*
(`NativeAudio.cs:222`), not per unit time. The call rate is whatever `WasapiOut` finds free
in the endpoint buffer, which grows under load — so the EMA's wall-clock time constant
*lengthens* precisely when the system is stressed and you want the controller to respond.
Correct form is `alpha = 1 − exp(−frames / (tc × sampleRate))`. Also `_depthAvg <= 0 ? depth : …`
uses the value as its own "uninitialised" sentinel, so at startup (depth = 0) it re-seeds
every call until the first sample arrives, then jumps `err` to `−1.0` and slams `trim` to
the `-1500` clamp — visible as line 265, `trim -1500ppm` in the first stats tick after
restart. Cosmetic, but it means every session begins with a maximum-rate ratio excursion.

**W12. `frames = count / _ch` truncates (`NativeAudio.cs:211`) while `Read` returns `count`
(`:308`).** If `count` were ever not frame-aligned, the trailing sample(s) of `buffer` are
left at whatever the caller had there — stale audio presented as new. `WasapiOut` always
requests whole frames so this cannot fire today, but it is an unguarded, unstated
precondition in the one function where a stale sample is a click.

**W13. The two detectors cannot see the artifact I am alleging, so "0 clicks / 0 dropouts"
does not clear the signal.**
- `clickscan.py:39` `thresh = max(p999*4, med*60, 2000)` finds *step discontinuities* only;
  `:43` collapses everything within 50 ms to one event, so a burst of zipper clicks
  (W7) counts as one.
- `dropscan.py:48` requires `2 <= ms <= 120` at `<2%` of typical level with loud audio on
  both sides — a 1 ms gap (48 samples), which is plainly audible, is invisible; so is any
  partial-level dip. `dropscan.py:26` is labelled "RMS envelope" but computes a **peak**
  envelope (`max(...)`), which makes it *less* sensitive to short dips, not more.
- Neither detector performs any spectral analysis, so a 6–25 % amplitude modulation of
  10–15 kHz content at 5–72 Hz (W5) produces **zero** detections in both. The tooling and
  the owner's ear are the same evidence class here, and the tooling is blind to the
  mechanism with the strongest supporting arithmetic. Absence of complaint plus absence of
  detections is weak evidence twice over, not once.

---

## 2. Correctness audit of `DriftTrim.Read`

Notation: `F = frames = count/_ch`; `r = ratio`; `T = tailFrames = _tailLen/_ch`;
`p₀` = value of `_pos` on entry; `P = p₀ + F·r` = value of `_pos` after the loop.

### 2.1 The read/consume ledger — exact, no drain

```
needFrames = ceil(p₀ + F·r) + 2 − T                  (:241)
avail      = (T·_ch + got)/_ch = T + needFrames       (:258, ReadFully ⇒ got = want_s)
           = ceil(P) + 2
consumed   = max(0, floor(P) − 1)                     (:291)
leftover   = avail − consumed = ceil(P) − floor(P) + 3 (:292)
```
So `leftover = 4` frames when `P ∉ ℤ`, `3` when `P ∈ ℤ`. `_tail` therefore holds 3–4 frames.

Telescoping the total source reads over N calls, with `readsₖ = availₖ − Tₖ` and
`Tₖ = leftoverₖ₋₁ = availₖ₋₁ − consumedₖ₋₁`:

```
Σₖ readsₖ = Σₖ availₖ − Σₖ availₖ₋₁ + Σₖ consumedₖ₋₁
          = avail_N − avail₀ + Σ consumedₖ
```
`avail` is bounded (`≈ F + 5`), and `Σ consumedₖ` is the total advance of `_pos`, which is
`Σ F·r` minus the bounded residual `_pos ∈ [1,2)`. Therefore

> **total frames read from the source = total frames consumed ± a bounded constant.**

The `+2` is a one-time offset, not a per-call surcharge. **The historical 4 ms/sec drain
cannot recur.** The `tailFrames` subtraction is doing exactly what the comment at `:236–239`
claims. Verdict: **correct**.

### 2.2 Can it drop, duplicate, or double-read a sample at a block boundary? No.

After the loop, `_pos -= consumed` (`:299`) gives
`_pos_new = P − (floor(P) − 1) = frac(P) + 1 ∈ [1, 2)`.
The tail copy (`:296`) moves source frames at absolute indices
`floor(P)−1, floor(P), floor(P)+1, floor(P)+2` into `_in[0..3]` of the next call.

Next call: `i1 = (int)_pos_new = 1` (since `_pos_new ∈ [1,2)`), `f = _pos_new − 1 = frac(P)`.
- `_in[i1] = _in[1]` = absolute frame `floor(P)` — which is exactly the `p1` the *previous*
  call would have used for its next output sample (`i1_old = floor(P)`).
- `f = frac(P)` — exactly the fractional offset the previous call would have used.
- `i0 = i1 − 1 = 0` = absolute frame `floor(P) − 1` — the genuine history sample.

The output sequence is therefore **bit-identical to what an infinite non-blocked buffer
would produce**. No sample is dropped, none is emitted twice, and no source frame is read
from `_src` twice (each frame is read once and then *carried*, not re-read).
Verdict: **correct**.

### 2.3 Bounds of the Catmull-Rom taps

Guard: `_pos + 2 < avail` (`:268`). With `i1 = floor(_pos) ≤ _pos`:
`i1 + 2 ≤ _pos + 2 < avail` ⇒ `i1 + 2 ≤ avail − 1`. The highest tap index is `i1+2`
(`:276`), so the highest float index touched is `(avail−1)·_ch + (_ch−1)` — the last valid
sample. **No out-of-bounds read is possible.** Verdict: **correct**.

### 2.4 Does the `i0` clamp fire once per block?

`i0 = i1 > 0 ? i1 − 1 : 0` (`:272`) clamps only when `i1 = 0`, i.e. `_pos < 1`.
By §2.2, `_pos ∈ [1,2)` after the first call, forever. `_pos` starts at `0` with `_tailLen = 0`,
so the clamp fires on the **first call of a stream only** — once, into what is startup
silence anyway (W6). It does **not** fire once per block. The stated design goal of the
`consumed − 1` carry is met. Verdict: **correct**; this is the strongest of the three
recent changes.

### 2.5 Can the loop under-produce, making the zero-fill fire?

The loop must survive `F` iterations. The binding case is the last one, entered with
`_pos = p₀ + (F−1)·r`; it requires
`p₀ + (F−1)·r + 2 < avail = ceil(p₀ + F·r) + 2`, i.e. `P − r < ceil(P)`.
Since `r > 0` and `ceil(P) ≥ P`, this is true unconditionally. **The loop always produces
`frames` samples**, so the zero-fill at `:304–306` is unreachable whenever `ReadFully`
holds — confirming the comment at `:301–303` and validating the decision to move the
starvation detector to `:253`. Verdict: **correct**.

### 2.6 Degenerate inputs

- `F = 0`: `needFrames = ceil(_pos) + 2 − T = 2 + 2 − 4 = 0`; `avail = 4`; loop produces 0;
  `consumed = 0`; `leftover = 4`; state unchanged. Stable, no drift. The
  `if (needFrames < 0) needFrames = 0` guard (`:242`) is provably unreachable given
  `_pos ≥ 1` and `T ≤ 4`, i.e. defensive only.
- `got < want_s` (a source that does not honour `ReadFully`): `avail` shrinks, the loop
  exits early on the guard, the zero-fill inserts silence **without advancing `_pos`**, and
  the tail/`_pos` bookkeeping stays consistent — so the stream resumes sample-accurate after
  a silent gap. Degrades correctly.
- Non-frame-aligned `count`: see W12. Only unguarded precondition found.

### 2.7 Summary of the audit

The index arithmetic in `DriftTrim.Read` is **correct on every axis examined**: no drop,
no duplication, no double-read, no out-of-bounds, no per-block clamp, no read/consume
imbalance, and the zero-fill path is provably dead. **All three recent changes are
implemented correctly.** Change (b) and change (c) also *achieve their stated goals*.
Change (a) is implemented correctly but **does not achieve its stated goal** (F5, W2) —
the artifact it targets is structural, not noise-driven.

---

## 3. Top 5 proposals (none of these is already done)

### P1 — Make capture event-driven: `new WasapiCapture(dev, true, 20)`
- **Mechanism.** `NativeAudio.cs:75` currently uses the 1-arg constructor, which NAudio
  2.2.1 documents as `useEventSync = false` → *"false use sleep"*
  (`NAudio.Wasapi.xml:3188`) with a 100 ms buffer, so `DoRecording` polls with
  `Thread.Sleep(~50 ms)`. Switching to event sync with a 20 ms buffer makes delivery track
  the WASAPI device period instead of a sleep timer.
- **Expected audible effect.** Removes the 60 ms burst measured at F3, so depth jitter
  collapses from ±25–30 ms to a few ms. Indirect but large: the controller (P2) then acts
  on a real signal, the AM rate (W5) stops being driven by burst-induced ratio swings, and
  `TargetMs` becomes reducible from 120 ms toward ~50 ms in a *later, separate* change
  (do not confound the A/B).
- **IMPACT 5 · CONFIDENCE 4 · EFFORT S** (one constructor argument change).
- **Cheap falsification test.** Add one line accumulating min/max `BufferedDuration` inside
  `DriftTrim.Read` and print it in `Stats()` instead of the instantaneous value. Run 60 s
  on the current build, then 60 s on the changed build. *Prediction:* current build shows
  min/max spread ≥ 55 ms per 30 s interval (F3 already proves ≥ 60 ms once); event-sync
  build shows ≤ 15 ms. If the spread does not fall, W1 is wrong and P1 should be reverted.

### P2 — Replace the deadband-P law with a PI whose integrator holds the true drift
- **Mechanism.** Keep the measurement, change the law (`NativeAudio.cs:224–227`):
  drop the hard deadband (or make it *soft*: subtract the deadband from `err` rather than
  zeroing the output, removing the 600 ppm discontinuity at the edge); add an integral term
  so `trim` can rest at a non-zero value equal to the true drift; reduce the clamp from
  ±1500 ppm to ±400 ppm (real drift is documented at ~100–270 ppm at `NativeAudio.cs:103–104`,
  so 1500 ppm is 6x more authority than the plant needs and only buys overshoot). Sketch:
  `i += ki*err*dt; trim = clamp(kp*softDeadband(err) + i, ±0.0004)` with `ki` sized for a
  ~30 s closed-loop time constant. Also fix the per-call → per-time alpha (W11).
- **Expected audible effect.** `trim` becomes a slowly-varying constant near the true drift
  instead of a ±1500 ppm relay. The W5 modulation rate stops sweeping and drops to a fixed
  ~5–15 Hz at ≤ 1/4 the current depth-of-sweep; buffer depth settles *at* 120 ms instead of
  parking on the 112.8 ms deadband edge, restoring 7 ms of the starvation margin for free.
- **IMPACT 5 · CONFIDENCE 4 · EFFORT S**.
- **Cheap falsification test — fully offline, no device.** ~40 lines of Python: model the
  plant as `depth' = drift − trim` with a disturbance drawn from the measured burst
  statistics (60 ms packets every ~50 ms, per F3), run (i) the current law and (ii) the PI.
  *Prediction:* the current law limit-cycles with tens-of-seconds period and full ±1500 ppm
  excursions, and reports `|trim| > 100 ppm` for ~60 % of steps — reproducing the observed
  `corr ≈ 61/s` (F5) from first principles; the PI settles with `max−min |trim| < 50 ppm`.
  If the simulated current law does *not* limit-cycle, my W2 model is wrong.
  In-app confirmation afterwards: `corr` per 30 s should fall from ~1840 to near 0.

### P3 — Register both audio threads with MMCSS "Pro Audio"
- **Mechanism.** NAudio 2.2.1 contains no `AvSetMmThreadCharacteristics` / `avrt` /
  `"Pro Audio"` reference at all (verified with `strings` over `NAudio.Wasapi.dll` and
  `NAudio.Core.dll`), so neither thread gets MMCSS scheduling. Fix without forking NAudio:
  `DriftTrim.Read` runs *on* the render thread and `DataAvailable` runs *on* the capture
  thread, so a one-shot `[ThreadStatic] bool` guard in each that P/Invokes
  `AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex)` and holds the returned handle
  registers both threads with ~6 lines and no library change.
- **Expected audible effect.** Removes the class of dropout where WebView2's GPU/compositor
  work at 1080p60 delays a render callback past the 80 ms endpoint buffer — the failure mode
  `Program.cs:166` records as a measured 66–99 ms gap. Does nothing for steady state; this is
  tail-latency insurance.
- **IMPACT 4 · CONFIDENCE 4 · EFFORT S**.
- **Cheap falsification test.** Track the maximum wall-clock gap between consecutive
  `DriftTrim.Read` entries and report it in `Stats()`. Run 60 s while deliberately loading
  the GPU/UI (resize the window, toggle fullscreen, drag it across monitors), before and
  after. *Prediction:* before, occasional gaps > 40 ms; after, max gap < 25 ms. If the
  before-run never exceeds ~25 ms, the scheduling hypothesis is dead and P3 should be dropped.

### P4 — Replace the Catmull-Rom kernel with a polyphase windowed-sinc
- **Mechanism.** The W5 table is the whole argument: Catmull-Rom's gain depends on the
  fractional offset `f` (flat at `f = 0`, −1.07 dB at 12 kHz at `f = 0.5`), and `f` walks at
  `|trim| × 48000` Hz, so HF content is amplitude-modulated at 6–25 % depth at 5–72 Hz.
  A 32-tap / 512-phase Kaiser-windowed sinc table (64 KB, built once at `Start()`, zero
  run-time allocation) has < 0.01 dB of phase-to-phase variation to 20 kHz — AM depth below
  0.1 %, i.e. 100x better. Same loop structure, same `_tail` carry (widen the history from
  1 to 15 frames and the guard from `_pos+2` to `_pos+16`; the §2 ledger is unchanged in
  form).
- **Expected audible effect.** Eliminates the fizz/roughness-on-bright-content mechanism
  outright, independently of whether P2 lands. This is the only proposal that removes the
  artifact rather than reducing its driver.
- **IMPACT 4 · CONFIDENCE 3 · EFFORT M**. Confidence 3 rather than 4 because the *audibility*
  of a 6 % AM at 10 kHz on game audio is arguable even though its *existence* is not.
- **Cheap falsification test — fully offline and replayable, needs no device.** Generate a
  synthetic 60 s WAV of 1 kHz + 12 kHz + 15 kHz sines; run it through the existing
  `DriftTrim` kernel at a *fixed* `ratio = 1.0015`; FFT the output. *Prediction:* sidebands
  around the 12 kHz carrier at ±72 Hz (and harmonics) at roughly −24 dBc, and around 15 kHz
  at roughly −18 dBc. Repeat with the sinc kernel: sidebands < −80 dBc. Then rerun the
  Catmull-Rom output through `deck/clickscan.py` and `deck/dropscan.py` — *prediction:*
  **both report zero events**, which is the point: it demonstrates on a known-bad signal
  that the existing detectors cannot see this class of defect (W13), and justifies adding a
  spectral test to `deck/`.

### P5 — Ramp the volume gain instead of stepping it
- **Mechanism.** `NativeAudio.cs:46` writes `_volumeNode.Volume` from the UI thread;
  `VolumeSampleProvider` multiplies with no smoothing, so each slider message is a gain
  discontinuity at an arbitrary sample and a drag is a burst of them. Replace with a target
  gain plus a one-pole smoother (~10 ms) applied inside the read loop, or fold the gain into
  `DriftTrim.Read`'s per-sample write (it is already touching every sample, so this is free).
  This also removes the cross-thread non-volatile float (`_volume`).
- **Expected audible effect.** Removes zipper noise / clicks during any volume change —
  a certain click source in a build whose measured click count is otherwise zero, firing
  exactly when the user interacts, which matches the "intermittent" character of the
  historical reports.
- **IMPACT 3 · CONFIDENCE 4 · EFFORT S**.
- **Cheap falsification test — uses tooling that already exists.**
  `ShadowCast.exe --loopback 30 clicks-before.wav` while dragging the volume slider across
  its range several times, then `python deck/clickscan.py clicks-before.wav`.
  *Prediction:* clicks clustered exactly at the drag times, `irregular` spacing. Repeat on
  the ramped build: 0 clicks. If the before-run shows 0 clicks, W7 is inaudible in practice
  and P5 is not worth doing. (Note `clickscan.py:43`'s 50 ms dedupe will *undercount* a drag
  burst, so any non-zero result is a floor, not a ceiling.)

### Also worth one line each (below the top 5, all EFFORT S)
- **Prime before play.** Delay `_output.Play()` (`NativeAudio.cs:116`) until
  `BufferedBytes ≥ TargetMs` worth, or zero the counters after priming. Today every session
  starts with ~80 ms of guaranteed silence and a permanent `starved` offset of 1–8 that
  masks all real events (W6).
- **Report distributions, not point samples.** `Stats()` (`:314`) should print min/max/mean
  of `depth` and `_trim` per interval. This single change would have made W2 self-evident
  from the log instead of requiring the `corr` cross-check (W3).
- **Crossfade the `drops` splice** or delete the path entirely and widen the PI clamp (W8).
- **Validate channels and encoding, not just rate** (`:82`) and log
  `_output.OutputWaveFormat` after `Init` so a silently-inserted NAudio resampler is
  visible (W9).
- **Wire `_output.PlaybackStopped`** to the log the way `RecordingStopped` already is (W10).
- **Fix the EMA to per-time alpha** and use an explicit `bool _depthInit` (W11).
- **Assert `count % _ch == 0`** at `:211` (W12).
- **Add a spectral test to `deck/`** (sideband/AM measurement); `clickscan` and `dropscan`
  between them cannot see any continuous-modulation artifact (W13).

---

## 4. Ranking — impact × confidence ÷ effort (S=1, M=2, L=3)

| # | Proposal | I | C | E | Score | Order |
|---|---|---|---|---|---|---|
| P1 | Event-driven capture (`WasapiCapture(dev, true, 20)`) | 5 | 4 | S | **20** | 1 |
| P2 | PI controller replacing deadband-P | 5 | 4 | S | **20** | 2 |
| P3 | MMCSS "Pro Audio" on both audio threads | 4 | 4 | S | **16** | 3 |
| P5 | Ramped volume gain | 3 | 4 | S | **12** | 4 |
| P4 | Polyphase windowed-sinc kernel | 4 | 3 | M | **6** | 5 |

**Sequencing note.** P1 before P2 even though they tie: P1 changes the disturbance the
controller must reject, so tuning P2 first means tuning against a disturbance you are about
to delete. Both are one-sitting changes; do them as two separate commits with a 60 s
`corr`/min-max reading between them, or you will not know which one moved the number.

**Do P4 last but do not drop it.** P1 and P2 shrink the *driver* of the interpolation AM
(they reduce `|trim|` and stop it sweeping); only P4 removes the artifact itself. Its
offline FFT test is the cheapest of the five and needs neither the dongle nor a rebuild of
the app, so it can be run today, before any code changes, to decide whether it is worth
scheduling at all.

---

## 5. Verdict on the three recent changes

| Change | Implemented correctly? | Achieves its stated goal? |
|---|---|---|
| (a) EMA of buffer depth (~0.5 s tc) | Yes (F2) — but the filter is time-variant (W11) | **No.** `corr` rate is unchanged/slightly higher after the change (F5: ~1400→~1840 per 30 s) and `trim` still hits the ±1500 ppm clamp (F4). The limit cycle is caused by the deadband-relay *structure*, which no amount of input filtering can remove (W2). The claim "trim now rests at exactly +0 ppm" is falsified by the app's own log. |
| (b) Catmull-Rom + `consumed−1` carry + `_pos+2<avail` | Yes — verified sound on every axis (§2.2–2.5) | **Partly.** ~2.4x flatter than linear, and the `i0` clamp provably fires once per stream rather than once per block. But the residual `f`-dependent HF droop still amplitude-modulates 10–15 kHz content by 6–25 % (W5), which is a *new* framing of an old artifact, not its removal. |
| (c) Starvation detector before the read | Yes — the old counter was genuinely unreachable under `ReadFully` (S5, §2.5) | **Partly.** The detector is in the right place, but its output is dominated by the startup transient (`_output.Play()` on an empty buffer) and then freezes, so it cannot function as a live alarm (W6). Prime before play and it becomes trustworthy. |

No new *correctness* problem was introduced by any of the three. The problem is that the
one change aimed at the audible symptom (a) aimed at the wrong layer, and the log has been
saying so for the last hour in a field (`corr`) that the headline field (`trim`) contradicts.
