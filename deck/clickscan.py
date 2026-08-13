#!/usr/bin/env python3
"""Detect click/pop discontinuities in a WAV.

A click is a step discontinuity: one sample jumps far further than the
surrounding audio ever does. The threshold is derived from the file's own
statistics (median jump and 99.9th percentile) rather than a fixed number, so
loud passages and quiet dialogue are judged on their own terms.

    python clickscan.py file.wav
"""
import statistics
import struct
import sys
import wave


def scan(path):
    w = wave.open(path, "rb")
    ch, width, rate, n = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
    raw = w.readframes(n)
    w.close()

    if width != 2:
        print(f"{path}: {width * 8}-bit not supported by this scanner")
        return None

    samples = struct.unpack("<%dh" % (len(raw) // 2), raw)
    mono = samples[::ch] if ch > 1 else samples
    diffs = [abs(mono[i] - mono[i - 1]) for i in range(1, len(mono))]
    if not diffs:
        print(f"{path}: no audio")
        return None

    med = statistics.median(diffs)
    srt = sorted(diffs)
    p999 = srt[int(len(srt) * 0.999)]
    # A click must tower over BOTH the typical jump and the 99.9th percentile,
    # so ordinary transients (plosives, percussion) do not register.
    thresh = max(p999 * 4, med * 60, 2000)

    events, last = [], -(10 ** 9)
    for i, d in enumerate(diffs):
        if d >= thresh and (i - last) > rate * 0.05:   # one event per 50ms
            events.append(i / rate)
            last = i

    dur = n / rate
    print(f"{path}")
    print(f"  {rate} Hz, {ch}ch, {dur:.1f}s | median jump {med}, p99.9 {p999}, threshold {thresh}")
    print(f"  CLICKS: {len(events)}  ({len(events) / max(dur, 1) * 60:.1f}/min)")
    if events:
        print("  first few (s): " + ", ".join(f"{t:.2f}" for t in events[:12]))
        if len(events) >= 3:
            gaps = [events[i] - events[i - 1] for i in range(1, len(events))]
            gmed = statistics.median(gaps)
            spread = statistics.pstdev(gaps) if len(gaps) > 1 else 0
            kind = "PERIODIC (drift-correction signature)" if spread < gmed * 0.25 else "irregular"
            print(f"  gap median {gmed:.2f}s, stdev {spread:.2f}s -> {kind}")
    return len(events)


if __name__ == "__main__":
    for p in sys.argv[1:]:
        scan(p)
