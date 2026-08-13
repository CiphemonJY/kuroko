"""Look for DROPOUTS as well as clicks.

A pop is not always a spike. The commoner failure is a brief mute - the stream
starves, output goes to (near) silence for a few ms, then resumes. That is
inaudible to a step-discontinuity detector but very audible to a listener, so
the earlier "0 clicks" result does not by itself clear the signal.
"""
import statistics
import struct
import sys
import wave

path = sys.argv[1]
w = wave.open(path, "rb")
ch, rate, n = w.getnchannels(), w.getframerate(), w.getnframes()
raw = w.readframes(n)
w.close()

s = struct.unpack("<%dh" % (len(raw) // 2), raw)
mono = [abs(v) for v in (s[::ch] if ch > 1 else s)]
dur = n / rate
print(f"{path}\n  {rate}Hz {ch}ch {dur:.1f}s")

# RMS envelope in 1ms blocks
blk = max(1, rate // 1000)
env = [max(mono[i:i + blk]) for i in range(0, len(mono) - blk, blk)]
loud = [e for e in env if e > 500]
if not loud:
    print("  content is essentially silent - inconclusive")
    sys.exit()

typical = statistics.median(loud)
print(f"  typical level {typical}, blocks {len(env)}")

# A dropout: >=2ms at <2% of typical level, with loud audio both sides.
floor = max(60, typical * 0.02)
events, i = [], 0
while i < len(env):
    if env[i] < floor:
        j = i
        while j < len(env) and env[j] < floor:
            j += 1
        ms = j - i
        before = max(env[max(0, i - 60):i] or [0])
        after = max(env[j:j + 60] or [0])
        # Only count gaps embedded in loud audio; ordinary musical rests have
        # quiet on at least one side and are not artifacts.
        if 2 <= ms <= 120 and before > typical * 0.5 and after > typical * 0.5:
            events.append((i / 1000.0, ms))
        i = j
    else:
        i += 1

print(f"  DROPOUTS: {len(events)}  ({len(events)/max(dur,1)*60:.1f}/min)")
for t, ms in events[:15]:
    print(f"    {t:7.2f}s  {ms}ms")
if len(events) >= 3:
    gaps = [events[k][0] - events[k-1][0] for k in range(1, len(events))]
    gm = statistics.median(gaps)
    sd = statistics.pstdev(gaps) if len(gaps) > 1 else 0
    print(f"  spacing median {gm:.2f}s stdev {sd:.2f}s -> "
          + ("PERIODIC" if sd < gm * 0.3 else "irregular"))
