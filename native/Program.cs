using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Kuroko;

/// <summary>Win32/DWM bits for the borderless glass window.</summary>
internal static class Native
{
    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    // Toggling Form.TopMost can force WinForms to RECREATE the window handle.
    // With our custom CreateParams and a hosted WebView2 that re-attaches the
    // browser and loses mouse tracking - the hover menu then never reappears.
    // SetWindowPos changes the z-order band without touching the handle.
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    internal static void SetAlwaysOnTop(IntPtr hwnd, bool on) =>
        SetWindowPos(hwnd, on ? HWND_TOPMOST : HWND_NOTOPMOST, 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS { public int L, R, T, B; }

    // DWM attributes (Win11): rounded corners, dark titlebar, backdrop material.
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    internal const int DWMWCP_ROUND = 2;
    internal const int DWMSBT_MAINWINDOW = 2;      // Mica
    internal const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    internal const int WM_NCLBUTTONDOWN = 0xA1;
    internal const int HTCAPTION = 2;

    internal const int WM_NCCALCSIZE = 0x0083;
    // Keeping these styles on a borderless window is what makes Windows treat
    // it as snappable/resizable; WM_NCCALCSIZE then removes the frame visually.
    internal const int WS_THICKFRAME = 0x00040000;
    internal const int WS_MAXIMIZEBOX = 0x00010000;
    internal const int WS_MINIMIZEBOX = 0x00020000;
    internal const int WS_SYSMENU = 0x00080000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0, rgrc1, rgrc2;
        public IntPtr lppos;
    }

    // --- job object: guarantees the audio helper dies with us ---------------
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr CreateJobObject(IntPtr sec, string? name);
    [DllImport("kernel32.dll")]
    internal static extern bool SetInformationJobObject(IntPtr job, int cls, IntPtr info, uint len);
    [DllImport("kernel32.dll")]
    internal static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    { public ulong R, W, O, RT, WT, OT; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    private const int ExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    /// <summary>
    /// A job whose children are killed when the handle closes - i.e. when this
    /// process exits for ANY reason, including being force-killed or crashing.
    /// Without this, ffplay is orphaned and keeps playing audio with no window
    /// to find or close (which is exactly what happened).
    /// </summary>
    internal static IntPtr CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var len = Marshal.SizeOf(info);
        var p = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, p, false);
            SetInformationJobObject(job, ExtendedLimitInformation, p, (uint)len);
        }
        finally { Marshal.FreeHGlobal(p); }
        return job;
    }

    /// <summary>Start a native move/resize drag on behalf of the page.</summary>
    internal static void BeginDrag(IntPtr hwnd, int hit)
    {
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(hit), IntPtr.Zero);
    }

    internal static int HitCode(string edge) => edge switch
    {
        "left" => 10, "right" => 11, "top" => 12, "topleft" => 13, "topright" => 14,
        "bottom" => 15, "bottomleft" => 16, "bottomright" => 17, _ => HTCAPTION,
    };
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // --mcp turns this same binary into the MCP server, so the toolchain
        // needs no Python. It never opens a window and never touches the
        // capture device; it proxies to whichever viewer instance is running.
        if (Array.Exists(args, a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase)))
            return McpServer.RunAsync().GetAwaiter().GetResult();

        // --loopback <seconds> <file.wav>
        // Records the exact digital signal being sent to the default output -
        // i.e. what the DAC receives. Every stage upstream measured clean while
        // pops were still audible, so this is the only way to tell whether the
        // discontinuity exists in the signal (software) or is introduced after
        // it (DAC, driver, or analog).
        var lb = Array.FindIndex(args, a => a.Equals("--loopback", StringComparison.OrdinalIgnoreCase));
        if (lb >= 0 && args.Length > lb + 2)
            return LoopbackRecorder.Run(int.Parse(args[lb + 1]), args[lb + 2]);

        // Audio runs in-process alongside a Chromium instance. A gen2 GC pause
        // stalls the capture/render callbacks for tens of ms, which is exactly
        // the 66-99ms gap measured at the 4-minute mark - far too long to be
        // clock drift. SustainedLowLatency avoids blocking collections while
        // the app is interactive.
        try
        {
            System.Runtime.GCSettings.LatencyMode =
                System.Runtime.GCLatencyMode.SustainedLowLatency;
        }
        catch { }

        // Last-resort logging. Without these a crash anywhere off the Load path
        // ends the process with an empty log and no window - indistinguishable
        // from "it never started".
        Application.ThreadException += (_, e) => MainForm.LogStatic($"FATAL (UI thread): {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => MainForm.LogStatic($"FATAL: {e.ExceptionObject}");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args));
        return 0;
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _web = new();
    private readonly string[] _args;
    private Process? _audio;

    // ffplay owns the audio pin. The dongle is 44.1kHz-only while every output
    // on this machine is locked to 48kHz, so something must resample; Chrome
    // corrects the resulting drift in periodic jumps that are audible as pops,
    // and page code cannot reach that stage. ffplay's aresample=async stretches
    // continuously instead. Video and audio are separate DirectShow devices, so
    // both can be open at once.
    // "ShadowCast 2" is the DONGLE's name as Windows enumerates it, not this
    // app's - it stays verbatim through the rename to Kuroko or capture breaks.
    private const string AudioDevice = "audio=Digital Audio Interface (2- ShadowCast 2)";

    public MainForm(string[] args)
    {
        _args = args;
        Text = "Kuroko";
        Width = 1280;
        Height = 720;
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("Kuroko.ico");
            if (s is not null) Icon = new System.Drawing.Icon(s);
        }
        catch { /* cosmetic only */ }
        BackColor = System.Drawing.Color.Black;
        StartPosition = FormStartPosition.CenterScreen;
        KeyPreview = true;
        MinimumSize = new System.Drawing.Size(560, 340);

        // Borderless floating window. The page draws its own title bar and
        // window buttons and asks the host to begin native move/resize drags,
        // because WebView2 covers the client area and would swallow the normal
        // WM_NCHITTEST that produces resize borders.
        FormBorderStyle = FormBorderStyle.None;

        _web.Dock = DockStyle.Fill;
        // Black, not transparent. Transparent let the Mica backdrop show through
        // the letterbox bars, and Mica is a wallpaper-tinted GREY - it lifts the
        // bars off black and sits right next to the picture, which is the one
        // place a video app must not have a grey reference. The glass panels
        // still read as glass: their backdrop-filter blurs the video behind
        // them, which it did before too.
        _web.DefaultBackgroundColor = System.Drawing.Color.Black;
        Controls.Add(_web);

        LoadSettings();
        // Saved value wins; otherwise fall back to -AudioDelayMs / the default.
        if (_audioDelayMs < 0) _audioDelayMs = AudioDelayMs;
        KillOrphanedAudio();

        _audioFallback.Tick += (_, _) =>
        {
            _audioFallback.Stop();
            if (!BrowserAudio && _audio is null) StartAudio();
        };

        // Keyboard shortcuts live in the page, so the WebView2 must actually
        // hold keyboard focus. A borderless form with custom chrome can be
        // foreground while the hosted control never takes focus - every key
        // then goes nowhere, with no error to show for it.
        Activated += (_, _) => FocusWeb();
        Shown += (_, _) => FocusWeb();
        Click += (_, _) => FocusWeb();

        // An unhandled throw in this async void handler used to take the process
        // down with nothing in the log - a WebView2 init failure looked like the
        // app simply never opened.
        Load += async (_, _) =>
        {
            try { ApplyGlass(); await InitAsync(); }
            catch (Exception ex)
            {
                Log($"FATAL during init: {ex}");
                MessageBox.Show($"Kuroko failed to start:\n\n{ex.Message}\n\nSee kuroko.log.",
                                "Kuroko", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        };
        FormClosed += (_, _) =>
        {
            _audioFallback.Stop();
            StopAudio();
            // Unlink the pad before we go. Leaving it attached would strand a
            // uinput device on the Deck holding js0 after the app that asked for
            // it is gone - deckpad's idle timer would eventually clear it, but
            // "eventually" is not good enough when it is sitting in front of the
            // controller you are trying to play with. Blocking briefly here is
            // fine; the window is already closed.
            if (_padLinked)
            {
                try { PadLinkAsync(false).Wait(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { Log($"pad unlink on close failed: {ex.Message}"); }
            }
            _api?.Dispose();
            _audio?.Dispose();       // ffplay's Process handle - never released before
            _audio = null;
        };
    }

    /// <summary>
    /// Keep the frame styles even though the window is borderless. Windows uses
    /// WS_THICKFRAME/WS_MAXIMIZEBOX to decide whether a window participates in
    /// Aero Snap (drag-to-edge, Win+Arrow, snap layouts); FormBorderStyle.None
    /// strips them, which is why snapping did nothing.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= Native.WS_THICKFRAME | Native.WS_MAXIMIZEBOX
                      | Native.WS_MINIMIZEBOX | Native.WS_SYSMENU;
            return cp;
        }
    }

    /// <summary>
    /// Swallow the non-client area so those styles cost us no visible frame or
    /// title bar. Maximised needs the working area applied explicitly, or the
    /// invisible frame pushes the window over the taskbar and onto neighbours.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_NCCALCSIZE && m.WParam != IntPtr.Zero && !_fullscreen)
        {
            if (WindowState == FormWindowState.Maximized)
            {
                var p = Marshal.PtrToStructure<Native.NCCALCSIZE_PARAMS>(m.LParam);
                var wa = Screen.FromHandle(Handle).WorkingArea;
                p.rgrc0 = new Native.RECT { Left = wa.Left, Top = wa.Top, Right = wa.Right, Bottom = wa.Bottom };
                Marshal.StructureToPtr(p, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            m.Result = IntPtr.Zero;   // client area == window area, no frame drawn
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>Give keyboard focus to the page, where the shortcuts live.</summary>
    private void FocusWeb()
    {
        // MoveFocus lives on the CONTROLLER, which the WinForms wrapper does not
        // expose; Select()+Focus() on the control is the supported route.
        try { _web.Select(); _web.Focus(); }
        catch { /* pre-init or disposed; harmless */ }
    }

    /// <summary>Rounded corners, dark mode, and a Mica backdrop (Win11).</summary>
    private void ApplyGlass()
    {
        try
        {
            var dark = 1;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            var round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

            // Mica needs the frame extended across the whole client area,
            // otherwise the backdrop is only drawn behind the (absent) titlebar.
            var m = new Native.MARGINS { L = -1, R = -1, T = -1, B = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);

            var backdrop = Native.DWMSBT_MAINWINDOW;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        }
        catch (Exception ex) { Log($"glass unavailable (older Windows?): {ex.Message}"); }
    }

    private bool _onTop;
    private bool _fullscreen;
    private FrameServer? _api;
    private int _navRetries;
    private string _pageUrl = "";

    /// <summary>
    /// Ask the page for a JPEG of the current frame. ExecuteScriptAsync returns
    /// a JSON string literal, and the payload is a data: URL, so both layers
    /// have to be peeled off before we have bytes.
    /// </summary>
    private async Task<byte[]?> GrabFrameAsync(int width, int quality)
    {
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            // WebView2 calls must happen on the UI thread; the HTTP server is not on it.
            BeginInvoke(async () =>
            {
                try
                {
                    var r = await _web.CoreWebView2.ExecuteScriptAsync(
                        $"window.scGrabFrame && window.scGrabFrame({width},{quality})");
                    tcs.TrySetResult(r);
                }
                catch (Exception ex) { Log($"grab failed: {ex.Message}"); tcs.TrySetResult(null); }
            });

            var json = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (string.IsNullOrWhiteSpace(json) || json == "null") return null;

            var dataUrl = System.Text.Json.JsonSerializer.Deserialize<string>(json);
            var comma = dataUrl?.IndexOf(',') ?? -1;
            if (dataUrl is null || comma < 0) return null;
            return Convert.FromBase64String(dataUrl[(comma + 1)..]);
        }
        catch (Exception ex) { Log($"grab error: {ex.Message}"); return null; }
    }

    private string ApiStatus()
    {
        try
        {
            var tcs = new TaskCompletionSource<string?>();
            BeginInvoke(async () =>
            {
                try
                {
                    var r = await _web.CoreWebView2.ExecuteScriptAsync("window.scStatus && window.scStatus()");
                    tcs.TrySetResult(r);
                }
                catch { tcs.TrySetResult(null); }
            });
            var json = tcs.Task.Wait(TimeSpan.FromSeconds(4)) ? tcs.Task.Result : null;
            if (string.IsNullOrWhiteSpace(json) || json == "null") return "{\"capturing\":false}";
            return System.Text.Json.JsonSerializer.Deserialize<string>(json) ?? "{\"capturing\":false}";
        }
        catch { return "{\"capturing\":false}"; }
    }
    private System.Drawing.Rectangle _restoreBounds;
    private FormWindowState _restoreState = FormWindowState.Normal;

    /// <summary>
    /// Real fullscreen: cover the monitor and strip the decorations that only
    /// make sense in a floating window. Rounded corners over a full screen leave
    /// four dark notches, and the extended (Mica) frame draws a seam along the
    /// edges - both read as a "strange border".
    /// </summary>
    private void SetFullscreen(bool on)
    {
        if (on == _fullscreen) return;
        _fullscreen = on;

        if (on)
        {
            _restoreState = WindowState;
            _restoreBounds = WindowState == FormWindowState.Normal
                ? Bounds : RestoreBounds;

            // Maximized would respect the taskbar; we want the whole monitor.
            WindowState = FormWindowState.Normal;
            Bounds = Screen.FromControl(this).Bounds;

            var square = 1;   // DWMWCP_DONOTROUND
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
            var none = new Native.MARGINS { L = 0, R = 0, T = 0, B = 0 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref none);
        }
        else
        {
            var round = Native.DWMWCP_ROUND;
            Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            var m = new Native.MARGINS { L = -1, R = -1, T = -1, B = -1 };
            Native.DwmExtendFrameIntoClientArea(Handle, ref m);

            // Order matters: leave the fullscreen size FIRST, then apply the
            // saved rectangle, then re-maximise if that is where we came from.
            // Assigning Bounds while still sized to the whole monitor left the
            // window full-screen-sized, so exiting fullscreen appeared to do
            // nothing until you clicked restore.
            WindowState = FormWindowState.Normal;

            var r = _restoreBounds;
            if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height)
            {
                // Nothing sane saved - centre a default window rather than
                // leaving it stuck at monitor size.
                var wa = Screen.FromControl(this).WorkingArea;
                r = new System.Drawing.Rectangle(
                    wa.X + (wa.Width - 1280) / 2, wa.Y + (wa.Height - 720) / 2, 1280, 720);
            }
            Bounds = r;

            if (_restoreState == FormWindowState.Maximized) WindowState = FormWindowState.Maximized;
        }
    }

    // Live-adjustable: the right value depends on how the video path is
    // behaving that day, and rebuilding to try a number is absurd. Changing it
    // restarts ffplay, which costs a brief gap - so it is applied on release,
    // not while dragging.
    private int _audioDelayMs = -1;
    private string _audioFix = "smooth";   // off | hard | smooth | soft

    /// <summary>
    /// Mix rate of the default output endpoint. Matching it exactly means the
    /// Windows mixer does no conversion of its own, removing a resampling stage
    /// we have no control over.
    /// </summary>
    private static int DefaultRenderRate()
    {
        try
        {
            using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            var dev = en.GetDefaultAudioEndpoint(NAudio.CoreAudioApi.DataFlow.Render,
                                                 NAudio.CoreAudioApi.Role.Console);
            var r = dev.AudioClient.MixFormat.SampleRate;
            return r is >= 8000 and <= 192000 ? r : 48000;
        }
        catch { return 48000; }
    }

    private int AudioDelayMs
    {
        get
        {
            for (int i = 0; i < _args.Length - 1; i++)
                if (_args[i].Equals("-AudioDelayMs", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(_args[i + 1], out var v)) return v;
            return 0;
        }
    }

    private int ApiPort
    {
        get
        {
            for (int i = 0; i < _args.Length - 1; i++)
                if (_args[i].Equals("-ApiPort", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(_args[i + 1], out var v)) return v;
            return 8791;
        }
    }

    private bool BrowserAudio =>
        Array.Exists(_args, a => a.Equals("-BrowserAudio", StringComparison.OrdinalIgnoreCase));

    private async Task InitAsync()
    {
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kuroko", "WebView2");
        Directory.CreateDirectory(userData);

        // --disable-features=... is not needed; the defaults already use the
        // MediaFoundation capture path. Keeping the switch list minimal avoids
        // accidentally disabling the GPU compositing we rely on.
        var opts = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
        };

        var env = await CoreWebView2Environment.CreateAsync(null, userData, opts);
        await _web.EnsureCoreWebView2Async(env);
        var core = _web.CoreWebView2;

        // Camera/mic granted silently - a prompt in a kiosk-style window would be
        // unusable - but ONLY to our own page. Anything else falls through to
        // State.Default, which prompts. IsLoopback covers 127.0.0.1/localhost/::1.
        core.PermissionRequested += (_, e) =>
        {
            if ((e.PermissionKind == CoreWebView2PermissionKind.Camera ||
                 e.PermissionKind == CoreWebView2PermissionKind.Microphone) &&
                Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) &&
                u.IsLoopback && u.Port == ApiPort)
            {
                e.State = CoreWebView2PermissionState.Allow;
            }
        };

        // Pin navigation to our own page. The permission check above guards an
        // ORIGIN, and %LOCALAPPDATA%\KurokoUI is user-writable while
        // FrameServer serves anything inside it - so a dropped evil.html would
        // share our origin and inherit silent camera+mic. Guarding the PAGE as
        // well as the origin is what actually closes that.
        core.NavigationStarting += (_, e) =>
        {
            if (_pageUrl.Length == 0) return;                     // not navigated yet
            var basePage = _pageUrl.Split('?')[0];
            if (!e.Uri.StartsWith(basePage, StringComparison.OrdinalIgnoreCase))
            {
                Log($"blocked navigation to {e.Uri}");
                e.Cancel = true;
            }
        };

        core.WebMessageReceived += OnWebMessage;

        // Screenshots and recordings are produced as browser downloads. Rewrite
        // the destination so they land in the chosen folder and never show a
        // save dialog or the download bubble.
        core.DownloadStarting += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(_saveDir);
                var name = Path.GetFileName(e.ResultFilePath);
                e.ResultFilePath = Path.Combine(_saveDir, name);
                e.Handled = true;                       // suppress the default UI
                e.DownloadOperation.StateChanged += (s2, _) =>
                {
                    if (s2 is CoreWebView2DownloadOperation op &&
                        op.State == CoreWebView2DownloadState.Completed)
                        BeginInvoke(() => Toast($"Saved to {_saveDir}"));
                };
            }
            catch (Exception ex) { Log($"download redirect failed: {ex.Message}"); }
        };

        core.NavigationCompleted += async (_, _) => { FocusWeb(); await PushSaveDirAsync(); };

        // HTML fullscreen only fills the WebView2 control - the WINDOW stays put,
        // so without this the page was "fullscreen" inside a normal-sized window.
        // Going truly fullscreen also has to drop the rounded corners and the
        // extended glass frame, which otherwise show as an odd border/corner
        // artifact along the screen edges.
        core.ContainsFullScreenElementChanged += (_, _) =>
            SetFullscreen(core.ContainsFullScreenElement);
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        // Map the local folder to a virtual HTTPS origin. This is what makes
        // getUserMedia work with no web server at all: https:// is a secure
        // context, whereas file:// is not and gets no camera access. It is why
        // the Python http.server dependency is gone.
        var webRoot = ExtractWeb();
        Log($"web root: {webRoot} (page present: {File.Exists(Path.Combine(webRoot, "mf-viewer.html"))})");

        // Surface load failures instead of leaving a blank/error page with no
        // explanation - this is how the ERR_ACCESS_DENIED regression showed up.
        // A failed load is retried rather than left as a dead window: the
        // virtual-host read can transiently fail (AV lock on a just-written
        // file), and the user should never be looking at a browser error page.
        core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess) { _navRetries = 0; return; }
            Log($"NAVIGATION FAILED: {e.WebErrorStatus} (attempt {_navRetries + 1})");
            if (_navRetries++ >= 4) { Log("giving up on navigation"); return; }
            var t = new System.Windows.Forms.Timer { Interval = 600 };
            t.Tick += (s2, _) =>
            {
                t.Stop(); t.Dispose();
                try { _web.CoreWebView2.Navigate(_pageUrl); } catch { }
            };
            t.Start();
        };

        // Audio deliberately starts LATER, when the page reports which device it
        // chose. Starting it here raced the page's getUserMedia permission
        // probe (which opens {video, audio} briefly to reveal device labels):
        // the probe grabbed the audio pin and ffplay died silently.
        if (!BrowserAudio) _audioFallback.Start();

        // The UI is served by our own loopback server (started below), because
        // 127.0.0.1 is a secure context and therefore valid for getUserMedia.
        var q = BrowserAudio ? "?autostart=1" : "?audio=external&autostart=1";
        _pageUrl = $"http://127.0.0.1:{ApiPort}/ui/mf-viewer.html{q}";
        // The server must exist BEFORE navigating - it now serves the UI too.
        try
        {
            _api = new FrameServer(ApiPort, GrabFrameAsync, ApiStatus, webRoot, ApiToken.Issue());
            Log($"local server on http://127.0.0.1:{ApiPort}/  (/ui open, /status + /frame token-gated)");
        }
        catch (Exception ex)
        {
            // FAIL CLOSED. This used to log and carry on to Navigate(), which
            // meant a second instance rendered the FIRST instance's page and
            // drove its capture - two windows, one device, and MCP tools hitting
            // whichever process happened to win the port. Bind failure almost
            // always means "already running", so say so and stop.
            Log($"FATAL: local server could not bind {ApiPort}: {ex.Message}");
            MessageBox.Show(
                $"Port {ApiPort} is already in use - Kuroko is probably already running.\n\n" +
                "Close the other window, or start this one with -ApiPort <n>.",
                "Kuroko", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close();
            return;
        }

        core.Navigate(_pageUrl);

        // Forward keys the page uses; WebView2 swallows some by default.
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F11) { SetFullscreen(!_fullscreen); e.Handled = true; }
            // Escape only LEAVES fullscreen. It used to test FormBorderStyle,
            // which is always None, so Escape toggled INTO fullscreen too.
            if (e.KeyCode == Keys.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; }
        };
    }

    // ToggleFullscreen() lived here and was a stale duplicate of SetFullscreen():
    // it branched on `FormBorderStyle != None`, which is ALWAYS false (the window
    // is borderless from construction), so the first F11 took the else branch and
    // gave you a native title bar. Worse, assigning FormBorderStyle recreates the
    // window handle - re-attaching WebView2 and killing its mouse tracking, the
    // exact hazard the Form.TopMost fix documents, reached by a second route.
    // Deleted; both keys now call the one correct implementation.

    private string _audioDevice = AudioDevice;
    private readonly IntPtr _job = Native.CreateKillOnCloseJob();

    /// <summary>
    /// Kill audio helpers left behind by a previous run that was force-killed
    /// before the job object existed (or if assigning to it ever fails). Matches
    /// on ffplay processes holding OUR capture device, so nothing else is
    /// touched - and this app is Kuroko.exe, never ffplay.exe, so there is
    /// no chance of matching our own command line.
    /// </summary>
    private static void KillOrphanedAudio()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ffplay"))
            {
                try { p.Kill(); Log($"killed orphaned ffplay pid {p.Id}"); } catch { }
            }
        }
        catch { }
    }

    // Where screenshots and recordings land. Persisted so it survives restarts.
    private string _saveDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Kuroko");

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kuroko", "settings.json");

    // --- virtual controller on the Deck -------------------------------------
    // The pad is DETACHED at rest: deckpad creates no input device until asked.
    // That is the whole point - a permanently-present pad takes js0 ahead of the
    // Deck's own controller and makes Steam re-shuffle controller defaults
    // around a device nobody is holding. So the app links it on request and
    // unlinks on close, and never links as a side effect of anything else.
    private string _padUrl = (Environment.GetEnvironmentVariable("KUROKO_PAD") ?? "").TrimEnd('/');
    private bool _padLinked;
    private static readonly HttpClient _padHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>Token file, ACL'd to this user - same pattern as the frame API's
    /// own token. Read fresh each call so rotating it does not need a restart
    /// (unlike the env var, which is fixed when the MCP child is spawned).</summary>
    private static string PadToken()
    {
        var env = Environment.GetEnvironmentVariable("KUROKO_PAD_TOKEN");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "Kuroko", "pad.token");
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        catch { return ""; }
    }

    private async Task<(bool ok, string note)> PadLinkAsync(bool link)
    {
        if (string.IsNullOrWhiteSpace(_padUrl))
            return (false, "No Deck configured - set padUrl in settings.json");
        var token = PadToken();
        if (string.IsNullOrWhiteSpace(token))
            return (false, "No pad token - set KUROKO_PAD_TOKEN or write %LOCALAPPDATA%\\Kuroko\\pad.token");
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Post,
                $"{_padUrl}/{(link ? "attach" : "detach")}")
            {
                Content = new StringContent("{\"reason\":\"kuroko app\"}",
                                            Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Deckpad-Token", token);
            var res = await _padHttp.SendAsync(req);
            if (!res.IsSuccessStatusCode)
                return (false, $"Deck replied {(int)res.StatusCode}");
            _padLinked = link;
            Log($"pad {(link ? "linked" : "unlinked")} ({_padUrl})");
            return (true, link ? "Controller linked on the Deck"
                               : "Controller unlinked - Steam sees no extra pad");
        }
        catch (Exception ex)
        {
            // Reaching the Deck can simply fail (asleep, off the tailnet). Report
            // it in the UI rather than leaving the button spinning forever.
            Log($"pad {(link ? "attach" : "detach")} failed: {ex.Message}");
            return (false, "Deck unreachable - is it awake and on the tailnet?");
        }
    }

    private void PushPadState(string note = "")
    {
        var js = $"window.scPadState && window.scPadState({(_padLinked ? "true" : "false")}," +
                 $"{System.Text.Json.JsonSerializer.Serialize(note)})";
        try { _ = _web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SettingsPath));
            if (doc.RootElement.TryGetProperty("saveDir", out var d))
            {
                var v = d.GetString();
                if (!string.IsNullOrWhiteSpace(v)) _saveDir = v;
            }
            if (doc.RootElement.TryGetProperty("volume", out var vol))
            {
                _volume = Math.Clamp((float)vol.GetDouble(), 0f, 1f);
                // Never restore a fully-muted volume: a mute that was saved (or
                // a crash while muted) would otherwise come back as "the app has
                // no sound" with no visible cause.
                if (_volume <= 0.001f) _volume = 1f;
            }
            if (doc.RootElement.TryGetProperty("audioDelayMs", out var ad))
                _audioDelayMs = Math.Clamp(ad.GetInt32(), 0, 500);
            if (doc.RootElement.TryGetProperty("audioFix", out var afx))
                _audioFix = afx.GetString() ?? "off";
            if (doc.RootElement.TryGetProperty("audioEngine", out var aeng))
                _audioEngine = aeng.GetString() ?? "native";
            if (doc.RootElement.TryGetProperty("padUrl", out var pu))
                _padUrl = (pu.GetString() ?? "").TrimEnd('/');
        }
        // Was a bare `catch { }`: a corrupt settings file silently reverted every
        // preference to defaults with nothing anywhere to explain it.
        catch (Exception ex) { Log($"settings load failed ({ex.Message}) - using defaults"); }
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var json = System.Text.Json.JsonSerializer.Serialize(
                new { saveDir = _saveDir, volume = _volume, audioDelayMs = _audioDelayMs,
                      audioFix = _audioFix, audioEngine = _audioEngine, padUrl = _padUrl });

            // Write-then-replace. A bare WriteAllText truncates first, so a crash
            // or power loss mid-write leaves a zero-length or half-written file -
            // and the loader above then quietly falls back to defaults. This is
            // written on every volume-slider sample, so the window is not small.
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, true);
        }
        catch (Exception ex) { Log($"settings save failed: {ex.Message}"); }
    }

    private float _volume = 1f;

    /// <summary>
    /// Push the slider value onto ffplay's audio session. The session does not
    /// exist until ffplay has actually started producing audio, so retry briefly
    /// rather than silently doing nothing on the first attempt.
    /// </summary>
    private void ApplyVolume(int attempt = 0)
    {
        // Native audio scales samples directly - no WASAPI session hunting,
        // and it takes effect on the very next buffer.
        if (_native is { Running: true }) { _native.Volume = _volume; return; }
        if (_audio is null || _audio.HasExited) return;
        if (AudioSession.SetVolume(_audio.Id, _volume)) return;
        if (attempt >= 10) { Log("volume: no audio session for ffplay yet"); return; }
        var t = new System.Windows.Forms.Timer { Interval = 400 };
        t.Tick += (s, _) => { t.Stop(); t.Dispose(); ApplyVolume(attempt + 1); };
        t.Start();
    }

    // If the page never reports a device (no audio input found, or it failed to
    // start), fall back to the known dongle pin so audio is not silently lost.
    private readonly System.Windows.Forms.Timer _audioFallback = new() { Interval = 9000 };

    // The page posts the audio device the user picked in the menu. ffplay needs
    // the DirectShow device NAME, which is exactly the label the browser
    // reports, so it can be passed straight through.
    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        var json = "";
        try
        {
            json = e.TryGetWebMessageAsString();
            var m = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var type = m.GetProperty("type").GetString();

            // --- window chrome, driven by the page's own title bar ---
            switch (type)
            {
                case "win-drag":
                    // Native move/resize. Doing it this way (rather than moving
                    // the form from mouse deltas) keeps snap-to-edge, Aero snap
                    // and multi-monitor DPI behaviour working for free.
                    var edge = m.TryGetProperty("edge", out var eEl) ? eEl.GetString() ?? "" : "";
                    Native.BeginDrag(Handle, Native.HitCode(edge));
                    return;
                case "win-min":
                    WindowState = FormWindowState.Minimized;
                    return;
                case "win-max":
                    WindowState = WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    return;
                case "win-close":
                    Close();
                    return;
                case "pad-link":
                    {
                        var link = m.TryGetProperty("link", out var lEl) && lEl.GetBoolean();
                        // Logged on ARRIVAL, before anything can fail. This is
                        // what splits the two possibilities: no line at all means
                        // the click never reached the host (a page problem); a
                        // line followed by a failure means the host tried and
                        // could not (config or the Deck).
                        Log($"pad-link message received: link={link}, padUrl=" +
                            (string.IsNullOrWhiteSpace(_padUrl) ? "<unset>" : _padUrl) +
                            $", token={(string.IsNullOrWhiteSpace(PadToken()) ? "<missing>" : "present")}");
                        _ = Task.Run(async () =>
                        {
                            var (ok, note) = await PadLinkAsync(link);
                            // Back to the UI thread: ExecuteScriptAsync is not
                            // callable from a pool thread, and the button is
                            // left disabled until this lands.
                            BeginInvoke(() => PushPadState(ok ? note : "Link failed: " + note));
                        });
                    }
                    return;
                case "win-top":
                    _onTop = !_onTop;
                    // NOT TopMost = ... : that can recreate the handle and kill
                    // the WebView2's mouse tracking, which is why the hover menu
                    // stopped appearing once the window was pinned.
                    Native.SetAlwaysOnTop(Handle, _onTop);
                    _ = _web.CoreWebView2.ExecuteScriptAsync(
                        $"window.scOnTop && window.scOnTop({(_onTop ? "true" : "false")})");
                    return;
            }

            if (type == "pick-folder")
            {
                using var dlg = new FolderBrowserDialog
                {
                    Description = "Where should screenshots and recordings go?",
                    UseDescriptionForTitle = true,
                    SelectedPath = Directory.Exists(_saveDir) ? _saveDir : "",
                };
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _saveDir = dlg.SelectedPath;
                    SaveSettings();
                    Log($"save folder set: {_saveDir}");
                    _ = PushSaveDirAsync();
                    Toast("Saving to " + _saveDir);
                }
                return;
            }

            if (type == "open-folder")
            {
                try
                {
                    Directory.CreateDirectory(_saveDir);
                    Process.Start(new ProcessStartInfo(_saveDir) { UseShellExecute = true });
                }
                catch (Exception ex) { Log($"open folder failed: {ex.Message}"); }
                return;
            }

            if (type == "jserror")
            {
                Log("PAGE JS ERROR: " + (m.TryGetProperty("msg", out var jm) ? jm.GetString() : "?"));
                return;
            }

            if (type == "audio-engine")
            {
                // Only one engine may hold the pin at a time, so always stop
                // everything before starting the new one.
                _audioEngine = m.GetProperty("mode").GetString() ?? "native";
                SaveSettings();
                Log($"audio engine -> {_audioEngine}");
                StopAudio();
                if (_audioEngine != "browser") StartAudio();
                _ = PushSaveDirAsync();
                return;
            }

            if (type == "audio-fix")
            {
                _audioFix = m.GetProperty("mode").GetString() ?? "off";
                SaveSettings();
                Log($"audio fix -> {_audioFix} (restarting audio)");
                if (!UseNative) { StopAudio(); StartAudio(); }
                _ = PushSaveDirAsync();
                return;
            }

            if (type == "audio-delay")
            {
                _audioDelayMs = Math.Clamp(m.GetProperty("ms").GetInt32(), 0, 500);
                SaveSettings();
                Log($"audio delay/buffer -> {_audioDelayMs}ms (restarting audio)");
                // For NATIVE this value is the buffer depth, so it must restart
                // too - that is the knob that trades latency for headroom.
                if (_audioEngine != "browser") { StopAudio(); StartAudio(); }
                _ = PushSaveDirAsync();
                return;
            }

            if (type == "volume")
            {
                var level = (float)m.GetProperty("level").GetDouble();
                _volume = Math.Clamp(level, 0f, 1f);
                SaveSettings();
                ApplyVolume();
                return;
            }

            if (type == "status")
            {
                var nv = m.GetProperty("video").GetInt32();
                var na = m.GetProperty("audio").GetInt32();
                Log($"stream tracks: video={nv} audio={na}" +
                    (na == 0 ? "  <-- recordings will be SILENT" : "  (recordings include audio)"));
                return;
            }

            if (type != "audio-device") return;
            var label = m.GetProperty("label").GetString();
            if (string.IsNullOrWhiteSpace(label)) return;

            _audioFallback.Stop();
            var wanted = "audio=" + NormalizeAudioLabel(label);
            if (wanted == _audioDevice && _audio is { HasExited: false }) return;
            _audioDevice = wanted;
            if (!BrowserAudio) { StopAudio(); StartAudio(); }
        }
        // A malformed message must not take the viewer down - but swallowing it
        // silently means a menu button that does nothing leaves NO evidence
        // anywhere, which is exactly how the pad-link button became
        // undiagnosable. Same defect this file already fixed for settings loads.
        catch (Exception ex) { Log($"web message failed ({json}): {ex.Message}"); }
    }

    // Chrome reports audio inputs as "Default - <name> (vvvv:pppp)", but
    // DirectShow only knows "<name>". Passing the browser label straight to
    // ffplay silently fails to open the device (which is exactly what happened:
    // the log showed audio=Default - Digital Audio Interface (2- ShadowCast 2)
    // (0100:1996) and ffplay exited immediately).
    private static string NormalizeAudioLabel(string label)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(
            label, @"^\s*(Default|Communications)\s*-\s*", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"\s*\([0-9a-fA-F]{4}:[0-9a-fA-F]{4}\)\s*$", "");
        return s.Trim();
    }

    private NativeAudio? _native;
    private readonly System.Windows.Forms.Timer _audioStats = new() { Interval = 30000 };
    private bool _statsWired;

    /// <summary>Exposed so the audio engine can log without a form reference.</summary>
    internal static void LogStatic(string msg) => Log(msg);

    /// <summary>
    /// ffplay is the DEFAULT and the native engine is opt-in via -NativeAudio.
    ///
    /// The native path (NativeAudio.cs) is written and works, but measured with
    /// chronic underruns - ~8/sec, buffer starved with the drift loop pinned at
    /// its slow clamp - which would be audible dropouts. Capture is not keeping
    /// pace with render for reasons not yet understood; it is not a tuning
    /// issue. Until that is solved, shipping it by default would trade working
    /// audio for broken audio.
    /// </summary>
    // Native is now the DEFAULT: it measures better than ffplay on the same
    // rig. ffplay resamples 44.1->48 (its DirectShow pin is 44.1-only) and
    // Windows then resamples again downstream; the native path captures via
    // WASAPI, which Windows already presents at 48kHz, so nothing is converted
    // and only ~100ppm of clock drift needs trimming.
    // Measured over 130s of loopback: 0 dropouts, 0 steady-state clicks,
    // versus a periodic 9ms silence gap every ~33s before the drift trim.
    // -Ffplay reverts to the old path.
    // Persisted engine choice, settable from the menu. It used to be CLI-only,
    // which meant the UI reported "ffplay" while native was actually running -
    // and left no way to switch or tune it.
    private string _audioEngine = "native";   // native | ffplay | browser

    private bool UseNative => _audioEngine == "native";

    private void StartAudio()
    {
        if (UseNative)
        {
            try
            {
                // _audioDelayMs (the LIVE, menu-settable value), not AudioDelayMs
                // (the CLI-only default). With the property here the A/V stepper
                // restarted audio and rebuilt it with the same number every time,
                // so on native the buffer-depth knob did nothing at all.
                _native = new NativeAudio(_audioDelayMs) { Volume = _volume };
                if (_native.Start())
                {
                    Log($"native audio: {_native.DeviceName} (target {_native.TargetMs}ms)");
                    // Drift is the failure mode that matters here, and it only
                    // shows over minutes - so leave a periodic trace rather
                    // than relying on someone noticing a pop.
                    // Subscribe ONCE: StartAudio runs again on every engine
                    // switch and A/V retune, and a bare += there accumulated a
                    // handler each time - the log was printing the same interval
                    // twice with two different buffer readings, which is exactly
                    // the sort of thing that gets misread as instability.
                    if (!_statsWired)
                    {
                        _statsWired = true;
                        _audioStats.Tick += (_, _) =>
                        {
                            if (_native is { Running: true }) Log("audio " + _native.Stats());
                        };
                    }
                    _audioStats.Start();
                    return;
                }
                Log("native audio: no capture device matched - falling back to ffplay");
                _native = null;
            }
            catch (Exception ex)
            {
                Log($"native audio failed ({ex.Message}) - falling back to ffplay");
                _native = null;
            }
        }

        var ffplay = FindTool("ffplay.exe");
        if (ffplay is null) { Log("ffplay.exe not found - video only"); return; }
        var device = _audioDevice;

        // adelay first, then the adaptive resampler: shift the stream, then let
        // aresample absorb drift on what comes out. The delay compensates for
        // ffplay's audio path being shorter than the video path.
        // async=1000 caps soft compensation at ~1000 samples/sec; once drift
        // exceeds that ffmpeg HARD-corrects, and a hard correction is exactly
        // the occasional pop. async=1 stretches as much as needed and
        // min_hard_comp pushes hard correction out to 100ms so it never fires.
        // NO first_pts=0: dshow timestamps start at device uptime (observed
        // start ~105568s), so asking the resampler to align output to pts 0
        // makes it pad enormously - that was the half-second of added delay.
        // Audio "fix" modes, cycled from the menu so this can be A/B'd by ear
        // instead of rebuilt. The source measures clean (no clipping, no
        // clicks), so any artifact is added by processing - which makes "off"
        // the honest baseline rather than an afterthought.
        //   off  - nothing at all. If drift is small this is transparent.
        //   soft - uncapped stretch. Kills pops, but continuous stretching is
        //          itself audible as fizz if it has to work hard.
        //   hard - capped compensation; less stretching, but hard corrections
        //          past the cap are pops.
        // ORDER MATTERS: resample first, THEN delay. Delaying first makes
        // aresample treat our own offset as drift and stretch to absorb it.
        //   off    - no processing at all.
        //   soft   - uncapped stretch; kills pops but stretches constantly = fizz.
        //   hard   - capped stretch, DEFAULT 0.1s hard-comp threshold. Sounded
        //            best but still popped: the pop IS the hard correction
        //            firing whenever drift outruns the cap inside that window.
        //   smooth - the combination not previously tried: bounded stretch (so
        //            it never works hard enough to fizz) AND a 0.5s threshold,
        //            so hard correction effectively never fires at all.
        // Output at the DEVICE's native rate. ffplay otherwise emits 44.1kHz
        // into a 48kHz endpoint, so Windows' mixer performs a SECOND resample
        // downstream of ours - two independent drift-correcting resamplers in
        // series, and the OS one corrects in audible jumps we cannot configure.
        // Measured: the capture itself is clean (0.024% shortfall over 60s, no
        // drops), so the discontinuity was being introduced after ffplay.
        var rate = DefaultRenderRate();
        string filt = _audioFix switch
        {
            "soft" => $"aresample={rate}:async=1:min_hard_comp=0.100",
            "hard" => $"aresample={rate}:async=1000",
            "smooth" => $"aresample={rate}:async=500:min_hard_comp=0.500",
            _ => $"aresample={rate}",   // still rate-match, just no drift work
        };
        if (_audioDelayMs > 0)
            filt = filt.Length > 0 ? $"{filt},adelay={_audioDelayMs}:all=1"
                                   : $"adelay={_audioDelayMs}:all=1";
        var af = filt;

        var psi = new ProcessStartInfo(ffplay)
        {
            // NO -fflags +nobuffer / -flags low_delay here. Those are the video
            // low-latency recipe; on an audio-only process they strip the jitter
            // margin and buy almost nothing, because audio latency is dominated
            // by the capture and device buffers instead. Verified offline that
            // the source and the resampler are both click-free, which leaves
            // playback starvation as the artifact source.
            // 80ms capture buffer: the extra 30ms is inaudible as delay and is
            // trimmable with the A/V stepper, whereas a dropout is not.
            Arguments = "-hide_banner -loglevel error -nodisp -autoexit " +
                        $"-f dshow -audio_buffer_size 80 -i \"{device}\" " +
                        (af.Length > 0 ? $"-af {af}" : ""),
            UseShellExecute = false,
            CreateNoWindow = true,
            // ffplay is a console subsystem binary. CreateNoWindow alone still
            // lets a console flash appear on some systems; redirecting its
            // streams means it never allocates one. They must be drained or a
            // full pipe would block ffplay.
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // NOT stdin: ffplay reads it for key commands, and a redirected
            // stdin hits EOF immediately, so ffplay exits and audio dies.
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            _audio = Process.Start(psi);
            if (_audio is not null)
            {
                // Tie it to a kill-on-close job so it cannot outlive us even if
                // this process is force-killed.
                if (_job != IntPtr.Zero)
                {
                    try { Native.AssignProcessToJobObject(_job, _audio.Handle); } catch { }
                }
                _audio.OutputDataReceived += (_, _) => { };
                _audio.ErrorDataReceived += (_, _) => { };
                _audio.BeginOutputReadLine();
                _audio.BeginErrorReadLine();
                Log($"audio started: {device}");
                ApplyVolume();   // restore the saved level onto the new session
            }
        }
        catch (Exception ex) { Log($"audio FAILED ({ffplay}): {ex.Message}"); }
    }

    // The page is embedded in the exe so the app is a single file. WebView2 can
    // only map a real folder to a virtual host, so write it out once per launch
    // (rewritten every time, so an updated build always wins).
    private static string ExtractWeb()
    {
        // NOT under %LOCALAPPDATA%\Kuroko: that is also WebView2's user-data
        // folder, and mapping a virtual host into that tree is refused with
        // ERR_ACCESS_DENIED. Keep the UI folder completely separate.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KurokoUI");
        Directory.CreateDirectory(dir);
        try
        {
            using var s = typeof(MainForm).Assembly.GetManifestResourceStream("mf-viewer.html");
            if (s is not null)
            {
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var bytes = ms.ToArray();
                var path = Path.Combine(dir, "mf-viewer.html");

                // Only rewrite when the content actually changed. Recreating the
                // file every launch makes it "new" to the AV scanner, which can
                // hold a lock just long enough for WebView2's first request to
                // come back ERR_ACCESS_DENIED - an intermittent blank window.
                var same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes);
                if (!same) File.WriteAllBytes(path, bytes);
            }
        }
        catch (Exception ex) { Log($"web extract failed: {ex.Message}"); }
        return dir;
    }

    // Tell the page the current folder so the menu can show it.
    private async Task PushSaveDirAsync()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "save-dir",
                dir = _saveDir,
                volume = _volume,
                external = !BrowserAudio,
                audioDelayMs = _audioDelayMs,
                // Was `audioMode`, which the page assigned to a variable nothing
                // ever read - so the engine never reached the menu and its label
                // stayed at the HTML default, "Audio: ffplay", while native was
                // running. The page-side half of this fix was written long ago
                // and reads `audioEngine`; the host was never updated to send
                // it, so the fix sat there landing on nothing.
                audioEngine = _audioEngine,
                audioFix = _audioFix,
            });
            await _web.CoreWebView2.ExecuteScriptAsync(
                $"window.dispatchEvent(new MessageEvent('sc-host',{{data:{json}}}))");
        }
        catch { }
    }

    private void Toast(string msg)
    {
        try
        {
            var esc = System.Text.Json.JsonSerializer.Serialize(msg);
            _ = _web.CoreWebView2.ExecuteScriptAsync($"window.scToast && window.scToast({esc})");
        }
        catch { }
    }

    private void StopAudio()
    {
        try { _audioStats.Stop(); } catch { }
        try { _native?.Dispose(); } catch { }
        _native = null;
        try { if (_audio is { HasExited: false }) _audio.Kill(); } catch { }
    }

    private static string? FindTool(string exe)
    {
        // Real install location FIRST. The winget shim in ...\WinGet\Links is an
        // app-execution alias (a reparse point), and Process.Start with
        // UseShellExecute=false + redirected streams FAILS on those - which is
        // exactly how audio died silently when PATH was searched first.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pkgRoot = Path.Combine(local, @"Microsoft\WinGet\Packages");
        try
        {
            if (Directory.Exists(pkgRoot))
            {
                foreach (var hit in Directory.EnumerateFiles(pkgRoot, exe, SearchOption.AllDirectories))
                    return hit;
            }
        }
        catch { }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var d = dir.Trim();
                if (d.Contains(@"WinGet\Links", StringComparison.OrdinalIgnoreCase)) continue;  // alias, not a binary
                var p = Path.Combine(d, exe);
                if (File.Exists(p)) return p;
            }
            catch { }
        }
        return null;
    }

    // Silent failure is what made this take three builds to find; leave a trail.
    private static void Log(string msg)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kuroko");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "kuroko.log"),
                $"{DateTime.Now:HH:mm:ss}  {msg}{Environment.NewLine}");
        }
        catch { }
    }
}



