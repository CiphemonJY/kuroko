using System;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Kuroko;

/// <summary>
/// Records the default output endpoint via WASAPI loopback - the exact digital
/// samples handed to the DAC.
///
/// This exists because every upstream stage measured clean (source 0 clicks,
/// filter chain 0 clicks, capture 0.024% shortfall over 60s, no double
/// playback) while pops remained audible. Loopback is the only place left that
/// can distinguish "the discontinuity is in the signal" from "the signal is
/// fine and something after it is at fault".
///
///   Kuroko.exe --loopback 60 C:\path\out.wav
/// </summary>
internal static class LoopbackRecorder
{
    internal static int Run(int seconds, string path)
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
            Console.Error.WriteLine($"loopback on: {dev.FriendlyName}");

            using var cap = new WasapiLoopbackCapture(dev);
            // Write 16-bit PCM: the click scanner reads s16, and the extra
            // precision of float is irrelevant for finding step discontinuities.
            var fmt = new WaveFormat(cap.WaveFormat.SampleRate, 16, cap.WaveFormat.Channels);
            var done = new ManualResetEventSlim(false);

            // Accumulate in MEMORY, write once at the end.
            //
            // This used to call WaveFileWriter.Write() straight from the capture
            // callback - synchronous disk I/O on an audio thread. A late write
            // shows up in the recording as a gap that never existed in the
            // actual output, so the instrument invents the exact fault it is
            // looking for. (Second time: the first was allocating per callback.)
            // 180s of 48k stereo s16 is ~34MB - trivially affordable.
            var sink = new System.IO.MemoryStream(
                Math.Clamp(seconds, 1, 600) * fmt.AverageBytesPerSecond + (1 << 20));

            // Reused across callbacks. Allocating per callback churns the heap
            // inside an audio path and provokes the very GC stalls this tool is
            // meant to detect - the instrument was manufacturing its own
            // dropouts (a 3.5s burst of them, measured).
            byte[] scratch = Array.Empty<byte>();

            cap.DataAvailable += (_, e) =>
            {
                // Same scheduling class as the thing being measured, or the
                // measurement is of the recorder, not the audio.
                Mmcss.JoinProAudio();

                // Loopback delivers IEEE float; convert to s16 with clamping so
                // an over-unity sample cannot wrap and masquerade as a click.
                if (cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    int n = e.BytesRecorded / 4;
                    if (scratch.Length < n * 2) scratch = new byte[n * 2];
                    var buf = scratch;
                    for (int i = 0; i < n; i++)
                    {
                        var f = BitConverter.ToSingle(e.Buffer, i * 4);
                        var s = (short)(Math.Clamp(f, -1f, 1f) * short.MaxValue);
                        buf[i * 2] = (byte)(s & 0xFF);
                        buf[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
                    }
                    sink.Write(buf, 0, n * 2);
                }
                else sink.Write(e.Buffer, 0, e.BytesRecorded);
            };
            cap.RecordingStopped += (_, _) => done.Set();

            cap.StartRecording();
            Thread.Sleep(Math.Clamp(seconds, 1, 600) * 1000);
            cap.StopRecording();
            done.Wait(4000);

            using var writer = new WaveFileWriter(path, fmt);
            writer.Write(sink.GetBuffer(), 0, (int)sink.Length);
            writer.Flush();

            Console.Error.WriteLine($"wrote {path} ({fmt.SampleRate}Hz {fmt.Channels}ch)");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("loopback failed: " + ex.Message);
            return 1;
        }
    }
}
