using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ShadowCast;

/// <summary>
/// In-process capture -> playback with NO resampling.
///
/// The whole audio saga has traced back to resampling. The dongle's DirectShow
/// pin is 44.1kHz-only, every output here is locked at 48kHz, so ffplay must
/// convert - and every artifact (fizz, pops) came from that conversion or from
/// a second one downstream.
///
/// But WASAPI presents the dongle's CAPTURE endpoint at 48kHz already: Windows'
/// audio engine does the 44.1->48 conversion itself, in the same well-tested
/// path it uses for every device on the machine. Capturing there means source
/// and sink are both 48kHz and we convert nothing at all.
///
/// What remains is drift between two independent clocks, handled by dropping or
/// duplicating a few ms only when the buffer reaches its limits - rare, and
/// bounded, unlike continuous stretching.
/// </summary>
internal sealed class NativeAudio : IDisposable
{
    private WasapiCapture? _capture;
    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private DriftTrim? _drift;
    private RampedVolume? _volumeNode;
    private System.Windows.Forms.Timer? _trim;

    internal string? DeviceName { get; private set; }

    /// <summary>
    /// Running means "actually moving audio", not "objects exist". The old
    /// version returned `_output is not null`, which stayed TRUE after the
    /// capture device vanished - so the stats line kept printing healthy-looking
    /// numbers over permanently dead audio. An accessor that cannot observe the
    /// failure it reports is worse than none.
    /// </summary>
    internal bool Running => _output is not null && !Lost;

    /// <summary>Set when capture stops unexpectedly (device unplugged/disabled).</summary>
    internal bool Lost { get; private set; }

    internal int TargetMs { get; }
    internal string RateInfo { get; private set; } = "";

    private long _drops, _pads;
    private string _deviceMatch = "ShadowCast";
    private int _recoverTicks;

    private float _volume = 1f;
    internal float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); if (_volumeNode is not null) _volumeNode.Target = _volume; }
    }

    // 120ms rather than 80: the buffer was swinging 58-95ms and occasionally
    // brushing the floor, and every time it does ReadFully pads with silence -
    // one audible tear per occurrence. The extra 40ms of headroom costs 40ms of
    // latency, which is still well under what ffplay's chain was adding.
    internal NativeAudio(int targetMs) => TargetMs = Math.Clamp(targetMs <= 0 ? 120 : targetMs, 30, 500);

    private static MMDevice? FindCapture(string match)
    {
        using var en = new MMDeviceEnumerator();
        var all = en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();
        // Never fall through to a microphone: capturing the room instead of the
        // console would be worse than failing outright.
        return all.FirstOrDefault(d => d.FriendlyName.Contains(match, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(d => d.FriendlyName.Contains("digital audio", StringComparison.OrdinalIgnoreCase));
    }

    internal bool Start(string deviceMatch = "ShadowCast")
    {
        try { return StartCore(deviceMatch); }
        catch (Exception ex)
        {
            // Without this, a throw anywhere after StartRecording()/Play()
            // leaves a LIVE capture and render session with no reference left to
            // stop them - audio keeps flowing from an object nobody owns.
            MainForm.LogStatic($"native audio: start failed: {ex.Message}");
            Stop();
            return false;
        }
    }

    private bool StartCore(string deviceMatch)
    {
        Stop();
        _deviceMatch = deviceMatch;
        var dev = FindCapture(deviceMatch);
        if (dev is null) return false;
        DeviceName = dev.FriendlyName;

        using var en = new MMDeviceEnumerator();
        var render = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

        // EVENT-DRIVEN, 20ms. The one-arg ctor is `useEventSync:false` with a
        // 100ms buffer - NAudio then SLEEPS between polls and hands us ~60ms of
        // audio in one atomic burst. That burst was the entire buffer-depth
        // jitter the EMA was added to smooth over: treating the symptom of a
        // polling loop. Event sync delivers on the device's own cadence.
        _capture = new WasapiCapture(dev, true, 20) { ShareMode = AudioClientShareMode.Shared };
        var inRate = _capture.WaveFormat.SampleRate;
        var outRate = render.AudioClient.MixFormat.SampleRate;
        var inCh = _capture.WaveFormat.Channels;
        var outCh = render.AudioClient.MixFormat.Channels;
        RateInfo = $"in {inRate}Hz/{inCh}ch / out {outRate}Hz/{outCh}ch";

        // If these ever disagree, bail rather than silently resampling: the
        // point of this engine is that it does not convert. CHANNELS matter as
        // much as rate - only the rate was checked before, so a non-stereo
        // render endpoint would have quietly reinserted an NAudio resampler and
        // undone the whole reason this path exists.
        if (inRate != outRate || inCh != outCh)
        {
            MainForm.LogStatic($"native audio: format mismatch ({RateInfo}) - not using native path");
            Stop();
            return false;
        }

        _buffer = new BufferedWaveProvider(_capture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(TargetMs * 6),
            DiscardOnBufferOverflow = true,   // never block the capture thread
            ReadFully = true,                 // pad with silence rather than stall
        };

        _capture.DataAvailable += (_, e) =>
        {
            Mmcss.JoinProAudio();     // first callback only; this IS the capture thread
            _buffer!.AddSamples(e.Buffer, 0, e.BytesRecorded);
        };
        var cap = _capture;
        _capture.RecordingStopped += (_, e) =>
        {
            // Identity check, not a flag: StopRecording() during a deliberate
            // Stop()/restart also raises this, and it can arrive AFTER Stop()
            // returns. If _capture is no longer this object we were superseded,
            // so this is a clean teardown, not a loss.
            if (!ReferenceEquals(_capture, cap)) return;

            // Losing capture used to be LOGGED AND DROPPED: _output was left
            // intact, Running kept saying true, and the stats line went on
            // printing plausible numbers over dead audio. Mark it lost so the
            // watchdog can rebuild - the dongle disappears from enumeration
            // entirely when HDMI goes away, so this fires on every source
            // power-off, not just on unplug.
            Lost = true;
            MainForm.LogStatic("native audio: capture stopped" +
                               (e.Exception is null ? " (device lost)" : $": {e.Exception.Message}"));
        };

        // Rates match, so this only has to absorb clock DRIFT - measured at
        // ~270ppm here (the buffer drained 9ms every 33s). Without it,
        // ReadFully pads the shortfall with silence, and that 9ms of silence is
        // the audible pop. Stretching by 0.027% instead is ~0.5 cents of pitch:
        // inaudible, and it never produces a discontinuity.
        _drift = new DriftTrim(_buffer.ToSampleProvider(), _buffer, TargetMs);
        _volumeNode = new RampedVolume(_drift) { Target = _volume, Immediate = true };

        // 80ms device buffer: more headroom for a scheduling hiccup to be
        // absorbed before the endpoint itself starves.
        _output = new WasapiOut(render, AudioClientShareMode.Shared, true, 80);
        _output.Init(_volumeNode);

        _capture.StartRecording();

        // PRE-ROLL TO FULL TARGET. Play() used to be called on an empty buffer,
        // which guaranteed silence at the head of every start and left a
        // permanent offset on every counter. Filling to only half target was
        // barely better: the controller then spent ~2.5 minutes pulling the
        // buffer up from 30ms with the trim pinned at its clamp the whole way.
        // Starting AT target means the loop begins where it wants to be, so the
        // only correction it ever has to make is real drift.
        for (int i = 0; i < 200 && _buffer.BufferedDuration.TotalMilliseconds < TargetMs * 0.95; i++)
            System.Threading.Thread.Sleep(5);
        MainForm.LogStatic($"native audio: pre-rolled to {_buffer.BufferedDuration.TotalMilliseconds:0}ms");

        _output.Play();
        _volumeNode.Immediate = false;   // ramp from here on; the first block must not fade in
        Lost = false;
        _recoverTicks = 0;

        // Doubles as the device-loss watchdog (see TrimDrift/TryRecover).
        _trim = new System.Windows.Forms.Timer { Interval = 1000 };
        _trim.Tick += (_, _) => TrimDrift();
        _trim.Start();
        return true;
    }

    private void TrimDrift()
    {
        if (Lost) { TryRecover(); return; }

        var b = _buffer;
        if (b is null) return;
        var ms = b.BufferedDuration.TotalMilliseconds;

        // Emergency only. With a PI controller the buffer should never get
        // here; this splice is uncrossfaded, so if it ever fires it is itself
        // an audible edit and wants to be LOUD in the log rather than silent.
        if (ms > TargetMs * 5)
        {
            var drop = (int)(b.WaveFormat.AverageBytesPerSecond * ((ms - TargetMs) / 1000.0));
            drop -= drop % b.WaveFormat.BlockAlign;
            var scratch = new byte[Math.Min(drop, b.BufferedBytes)];
            b.Read(scratch, 0, scratch.Length);
            _drops++;
            MainForm.LogStatic($"native audio: EMERGENCY SPLICE, dropped {scratch.Length / (double)b.WaveFormat.AverageBytesPerSecond * 1000:0}ms " +
                               "(controller failed to hold the buffer - this is audible)");
        }
        else if (ms < TargetMs * 0.25)
        {
            _pads++;
        }
    }

    /// <summary>
    /// Rebuild after device loss. Throttled, because the dongle is gone for as
    /// long as the HDMI source is off and retrying every second forever would
    /// spam the log with something the user already knows.
    /// </summary>
    private void TryRecover()
    {
        if (++_recoverTicks % 3 != 0) return;      // ~3s between attempts
        var match = _deviceMatch;
        MainForm.LogStatic("native audio: attempting recovery...");
        try
        {
            if (Start(match))
            {
                MainForm.LogStatic("native audio: RECOVERED");
                return;
            }
        }
        catch (Exception ex) { MainForm.LogStatic($"native audio: recovery failed: {ex.Message}"); }
        // Start() calls Stop() first, which clears the timer - so if it failed
        // we need a timer again to keep trying.
        EnsureWatchdog();
    }

    private void EnsureWatchdog()
    {
        if (_trim is not null) return;
        Lost = true;
        _trim = new System.Windows.Forms.Timer { Interval = 1000 };
        _trim.Tick += (_, _) => TrimDrift();
        _trim.Start();
    }

    internal void Stop()
    {
        try { _trim?.Stop(); _trim?.Dispose(); } catch { }
        try { _capture?.StopRecording(); } catch { }
        try { _output?.Stop(); _output?.Dispose(); } catch { }
        try { _capture?.Dispose(); } catch { }
        _trim = null; _output = null; _capture = null; _buffer = null; _volumeNode = null;
    }

    internal string Stats() => _buffer is null
        ? "idle"
        : Lost
            ? "DEVICE LOST - audio is not playing, watchdog retrying"
            : $"{RateInfo}, buffer {_buffer.BufferedDuration.TotalMilliseconds:0}ms " +
              $"(target {TargetMs}ms), drops {_drops}, pads {_pads}, {_drift?.Stats()}";

    public void Dispose() => Stop();
}

/// <summary>
/// Joins the calling thread to the MMCSS "Pro Audio" scheduling class.
///
/// Once per thread, from inside the audio callback. Deliberately NOT reverted:
/// the registration dies with the thread, and calling AvRevert from a teardown
/// path on a different thread than the one that joined is a no-op at best.
/// </summary>
internal static class Mmcss
{
    [System.Runtime.InteropServices.DllImport("avrt.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint taskIndex);

    [ThreadStatic] private static bool _joined;

    internal static void JoinProAudio()
    {
        if (_joined) return;
        _joined = true;          // set FIRST: never retry inside an audio callback
        try
        {
            uint idx = 0;
            var h = AvSetMmThreadCharacteristicsW("Pro Audio", ref idx);
            if (h == IntPtr.Zero) MainForm.LogStatic("mmcss: Pro Audio join failed (audio still works, just lower priority)");
        }
        catch { /* avrt missing: not fatal, only less resilient under load */ }
    }
}

/// <summary>
/// Volume with a per-sample ramp.
///
/// VolumeSampleProvider applies a new gain to the very next sample, so a slider
/// drag is a burst of step discontinuities straight into the render buffer -
/// each one a click, from a control the user is holding. Ramping over ~15ms
/// makes the same change inaudible.
/// </summary>
internal sealed class RampedVolume : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly float _step;
    private float _current;

    public WaveFormat WaveFormat => _src.WaveFormat;

    /// <summary>Where the gain is heading. Safe to set from any thread.</summary>
    internal float Target { get; set; } = 1f;

    /// <summary>Skip the ramp on the next block (used for the very first one).</summary>
    internal bool Immediate { get; set; }

    internal RampedVolume(ISampleProvider src)
    {
        _src = src;
        // ~15ms full-scale traverse, expressed per frame.
        _step = 1f / (src.WaveFormat.SampleRate * 0.015f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _src.Read(buffer, offset, count);
        int ch = WaveFormat.Channels;

        if (Immediate) { _current = Target; Immediate = false; }

        for (int i = 0; i < read; i += ch)
        {
            if (_current != Target)
            {
                var d = Target - _current;
                _current += Math.Abs(d) <= _step ? d : Math.Sign(d) * _step;
            }
            for (int c = 0; c < ch && i + c < read; c++)
                buffer[offset + i + c] *= _current;
        }
        return read;
    }
}

/// <summary>
/// Absorbs clock drift by resampling at a ratio a hair either side of 1.0.
///
/// This is NOT rate conversion - input and output are the same rate. It exists
/// only because two independent 48kHz crystals are never exactly equal, so the
/// buffer creeps in one direction until something gives. Padding the shortfall
/// with silence is what produced a 9ms gap every 33 seconds.
///
/// Correction is bounded to +-0.2% (about 3 cents) and slew-limited so the
/// ratio glides; an abruptly changing ratio is itself audible.
/// </summary>
internal sealed class DriftTrim : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly BufferedWaveProvider _buf;
    private readonly int _ch;
    private readonly double _targetMs;

    private float[] _in = Array.Empty<float>();
    private float[] _tail = Array.Empty<float>();
    private int _tailLen;
    private double _pos;          // fractional read position within _in
    private double _trim;         // current correction, ~0
    private double _depthAvg;     // smoothed buffer depth the controller acts on
    private double _integral;     // PI integral term, ms of accumulated error
    private double _trimMin, _trimMax;   // range since the last Stats() call
    private long _starved, _starvedFrames;
    private bool _primed;         // has the buffer ever reached target? (see below)

    public WaveFormat WaveFormat => _src.WaveFormat;

    internal DriftTrim(ISampleProvider src, BufferedWaveProvider buf, int targetMs)
    {
        _src = src;
        _buf = buf;
        _ch = src.WaveFormat.Channels;
        _targetMs = targetMs;
        // Pre-size generously so the audio path never allocates at run time.
        // An allocation here can trigger a collection, and a collection is a
        // stall, and a stall is the artifact we are trying to remove.
        _in = new float[src.WaveFormat.SampleRate * _ch];      // 1s of headroom
        _tail = new float[src.WaveFormat.SampleRate * _ch / 4];
    }

    public int Read(float[] buffer, int offset, int count)
    {
        // This runs ON the WASAPI render thread. NAudio never joins MMCSS
        // (verified: no avrt/AvSetMmThreadCharacteristics reference anywhere in
        // NAudio.Wasapi or NAudio.Core), so the thread feeding the DAC competes
        // with ordinary work - and this app renders 1080p60 in the same process.
        // A late callback is a dropout no amount of controller tuning prevents.
        Mmcss.JoinProAudio();

        int frames = count / _ch;

        // Steer the buffer toward target. Deadband so it can rest at exactly
        // 1.0; tiny gain and slew so the ratio never jumps.
        double depth = _buf.BufferedDuration.TotalMilliseconds;
        // Light smoothing only. With event-driven capture the 60ms polling
        // bursts are gone, so this no longer has to hide a sawtooth - it just
        // takes the edge off block granularity.
        _depthAvg = _depthAvg <= 0 ? depth : _depthAvg + (depth - _depthAvg) * 0.05;

        // PI, CONTINUOUS - no deadband.
        //
        // The old law was `|err| < 0.06 ? 0 : err * 0.01`, which JUMPS from 0 to
        // 600ppm the instant the error crosses the deadband edge. That is a
        // RELAY, not a proportional controller, and the plant is an integrator
        // (buffer depth is the integral of rate error), so the loop limit-cycled
        // and parked at the lower deadband edge - which is exactly where the
        // logged average sat (106-117ms against a 120ms target). Smoothing the
        // INPUT cannot fix a discontinuous LAW.
        //
        // Proportional alone would still leave standing error, because holding a
        // constant ~270ppm crystal drift requires a constant non-zero output and
        // P can only produce that from a non-zero error. The integral term is
        // what lets the output stay put while the error goes to zero.
        double dtMs = frames * 1000.0 / WaveFormat.SampleRate;
        double err = (_depthAvg - _targetMs) / _targetMs;

        // kp from the plant: d(depth)/dt = -trim, so tau = target/kp. 0.002 on a
        // 120ms target is a ~60s settle - slow on purpose. Pitch error is the
        // thing being minimised, not settling time.
        const double kp = 0.002;
        const double ki = kp / 120000.0;   // integral time ~120s, in ms
        const double maxTrim = 0.001;      // +-1000ppm ~ 1.7 cents

        // Anti-windup: only integrate while the output is off its limits,
        // otherwise a long device stall winds up a correction that then takes
        // minutes to unwind and sounds like a slow pitch bend.
        if (Math.Abs(_trim) < maxTrim * 0.99) _integral += err * dtMs;
        double u = Math.Clamp(kp * err + ki * _integral, -maxTrim, maxTrim);

        // Slew is a safety net now, not the control law. It only binds on a
        // discontinuity (a device glitch), never in normal operation.
        _trim += Math.Clamp(u - _trim, -0.00002, 0.00002);
        if (_trim < _trimMin) _trimMin = _trim;
        if (_trim > _trimMax) _trimMax = _trim;
        double ratio = 1.0 + _trim;

        // Read EXACTLY the shortfall. Reading a couple of frames "spare" each
        // call looks harmless but drains the source buffer at ~4ms/sec no
        // matter what the ratio says - the reads, not the interpolation, are
        // what empty it. Account for the carried tail and the fractional
        // position so total reads track total consumption.
        // +2 rather than +1: cubic interpolation reads one sample past the
        // bracket. Over-reading is safe ONLY because the unconsumed remainder is
        // carried and tailFrames subtracted here - that is what makes total
        // reads track total consumption. Drop the subtraction and any spare read
        // drains the source buffer regardless of ratio (the 68-clicks/min bug).
        int tailFrames = _tailLen / _ch;
        int needFrames = (int)Math.Ceiling(_pos + frames * ratio) + 2 - tailFrames;
        if (needFrames < 0) needFrames = 0;
        int want_s = needFrames * _ch;
        if (_in.Length < want_s + _tailLen) _in = new float[want_s + _tailLen + _ch * 8];

        // Measure the shortfall BEFORE reading. The source is a
        // BufferedWaveProvider with ReadFully=true, which always returns the
        // full count and pads any deficit with silence INTERNALLY - so the read
        // can never report starvation and the zero-fill loop below can never
        // fire. Counting only that loop gives a permanent, meaningless zero.
        // The only place the truth is visible is the buffer level just before
        // the read, so take it here.
        int haveFrames = _buf.BufferedBytes / _buf.WaveFormat.BlockAlign;
        // Only count starvation once the buffer has actually filled once.
        // Counting from the first sample charged every start with its own fill
        // time and left a permanent offset - "starved 8 (150ms)" that looked
        // like a live fault but was pure startup. A counter that always reads
        // non-zero is as useless as one that always reads zero.
        if (!_primed && depth >= _targetMs * 0.9) _primed = true;
        if (_primed && haveFrames < needFrames) { _starved++; _starvedFrames += needFrames - haveFrames; }

        Array.Copy(_tail, 0, _in, 0, _tailLen);
        int got = want_s > 0 ? _src.Read(_in, _tailLen, want_s) : 0;
        int avail = (_tailLen + got) / _ch;
        _tailLen = 0;

        // Catmull-Rom rather than linear. Linear interpolation is a crude
        // lowpass whose response depends on the fractional offset, so it both
        // dulls the signal and modulates that dulling as the offset walks -
        // broadband fuzz that reads as crackle. Cubic costs a few more FLOPs and
        // is dramatically flatter. It needs one sample of history, which is why
        // the carry below keeps consumed-1.
        int produced = 0;
        while (produced < frames && _pos + 2 < avail)
        {
            int i1 = (int)_pos;
            double f = _pos - i1;
            int i0 = i1 > 0 ? i1 - 1 : 0;
            for (int c = 0; c < _ch; c++)
            {
                float p0 = _in[i0 * _ch + c], p1 = _in[i1 * _ch + c];
                float p2 = _in[(i1 + 1) * _ch + c], p3 = _in[(i1 + 2) * _ch + c];
                double v = p1 + 0.5 * f * ((p2 - p0)
                         + f * ((2 * p0 - 5 * p1 + 4 * p2 - p3)
                         + f * (3 * (p1 - p2) + p3 - p0)));
                buffer[offset + produced * _ch + c] = (float)v;
            }
            _pos += ratio;
            produced++;
        }

        // Carry the unconsumed remainder so no sample is ever dropped at a
        // block boundary - a lost sample there would be its own click. Keep one
        // consumed sample as history too, so the cubic kernel never has to clamp
        // at a block edge; clamping there would fire once per block, and a
        // block-rate artifact is exactly the kind that becomes a tone.
        int consumed = Math.Max(0, (int)_pos - 1);
        int leftover = Math.Max(0, avail - consumed);
        if (leftover > 0)
        {
            if (_tail.Length < leftover * _ch) _tail = new float[leftover * _ch + _ch * 8];
            Array.Copy(_in, consumed * _ch, _tail, 0, leftover * _ch);
            _tailLen = leftover * _ch;
        }
        _pos -= consumed;

        // Belt and braces: ReadFully means this should be unreachable, so it is
        // deliberately NOT counted - the shortfall check above is the real
        // detector. Emit silence rather than repeat stale audio.
        for (; produced < frames; produced++)
            for (int c = 0; c < _ch; c++)
                buffer[offset + produced * _ch + c] = 0f;

        return count;
    }

    /// <summary>
    /// Reports trim as a RANGE over the interval, not a point sample.
    ///
    /// The old version printed the instantaneous value, and I read four
    /// consecutive 30s samples of a value that updates ~100x/second, concluded
    /// "resting at +0ppm", and reported that as evidence the loop was quiet. It
    /// was aliasing: over the same period trim was reaching both clamps. A
    /// periodic sample of a fast signal is not a summary of it.
    /// </summary>
    internal string Stats()
    {
        var ms = _starvedFrames * 1000.0 / WaveFormat.SampleRate;
        var lo = _trimMin * 1e6;
        var hi = _trimMax * 1e6;
        _trimMin = _trimMax = _trim;      // range is per-interval, so reset it
        // `corr` used to be here: a count of Read() calls where |trim| exceeded
        // 100ppm. It looked like an event count but was really "how many audio
        // blocks elapsed", so it climbed by ~3000 every 30s regardless and told
        // nobody anything. The min..max range above says what it was trying to.
        return $"trim {lo:+0;-0}..{hi:+0;-0}ppm, avg {_depthAvg:0}ms, " +
               $"starved {_starved} ({ms:0.0}ms silence)";
    }
}
