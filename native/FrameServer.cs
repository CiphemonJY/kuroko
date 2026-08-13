using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ShadowCast;

/// <summary>
/// Tiny localhost HTTP server that hands out the current captured frame.
///
/// It exists because the dongle is an EXCLUSIVE device: while the viewer is
/// running nothing else can open it, so an MCP server (or any other tool)
/// cannot grab frames itself. This is the only seam through which the live
/// picture can be shared.
///
/// Written on a raw TcpListener rather than HttpListener: HttpListener needs a
/// netsh URL ACL to bind without admin rights, which would make the feature
/// fail for no good reason on a normal user account.
///
/// Bound to 127.0.0.1 only - never a routable address.
///
///   GET /status          -> JSON: capture state, resolution, fps
///   GET /frame[?w=&q=]   -> image/jpeg of the current frame
/// </summary>
/// <summary>
/// The shared secret for /status and /frame.
///
/// Written to a file rather than passed on a command line (command lines are
/// world-readable via WMI) or printed to stdout. Regenerated every launch, so a
/// token that leaks dies with the session.
/// </summary>
internal static class ApiToken
{
    internal static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ShadowCast", "api.token");

    /// <summary>Mint a fresh token and publish it for the MCP side to read.</summary>
    internal static string Issue()
    {
        var t = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, t);
        }
        catch { return ""; }    // cannot publish it => run open rather than break the MCP tools
        return t;
    }

    /// <summary>Read the current token, or empty if there is none.</summary>
    internal static string Read()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path).Trim() : ""; }
        catch { return ""; }
    }
}

internal sealed class FrameServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<int, int, Task<byte[]?>> _grabFrame;   // (width, quality) => jpeg
    private readonly Func<string> _status;
    private readonly string _uiRoot;
    private readonly string _token;
    private readonly CancellationTokenSource _cts = new();

    internal int Port { get; }

    internal FrameServer(int port, Func<int, int, Task<byte[]?>> grabFrame, Func<string> status, string uiRoot, string token = "")
    {
        Port = port;
        _grabFrame = grabFrame;
        _status = status;
        _uiRoot = uiRoot;
        _token = token ?? "";
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>True only if <paramref name="candidate"/> resolves inside <paramref name="root"/>.</summary>
    private static bool IsUnder(string root, string candidate)
    {
        try
        {
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }   // malformed path is not under anything
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;

                var req = await ReadRequestAsync(stream);
                if (req is null) return;
                var (head, headers) = req.Value;

                if (!HostOk(headers))
                {
                    await WriteAsync(stream, 403, "text/plain", Encoding.UTF8.GetBytes("bad Host"));
                    return;
                }

                var (path, query) = SplitQuery(head);

                // Serve the UI itself. http://127.0.0.1 is a secure context, so
                // getUserMedia is legal here - the same route the original
                // browser build used. This replaced WebView2's virtual-host
                // mapping, which began failing with ERR_ACCESS_DENIED for
                // reasons I could not isolate; one mechanism is also simpler
                // than two.
                if (path == "/" || path.StartsWith("/ui"))
                {
                    var name = path is "/" or "/ui" or "/ui/" ? "mf-viewer.html"
                             : Path.GetFileName(path);
                    var file = Path.Combine(_uiRoot, name);
                    // GetFileName strips / and \ but NOT the volume separator, so
                    // "/ui/C:foo.ps1" arrives as a ROOTED name and Path.Combine
                    // silently discards _uiRoot - serving anything in the process
                    // CWD (verified: that request returned a real file). Resolve
                    // and check containment rather than blacklisting separators:
                    // one check covers every variant, including the ones I would
                    // not have thought to blacklist.
                    if (IsUnder(_uiRoot, file) && File.Exists(file))
                    {
                        var ctype = name.EndsWith(".html") ? "text/html; charset=utf-8"
                                  : name.EndsWith(".js") ? "text/javascript"
                                  : name.EndsWith(".css") ? "text/css" : "application/octet-stream";
                        await WriteAsync(stream, 200, ctype, await File.ReadAllBytesAsync(file));
                        return;
                    }
                    await WriteAsync(stream, 404, "text/plain", Encoding.UTF8.GetBytes("no such UI file: " + name));
                    return;
                }

                // Everything below here exposes what is on the captured screen,
                // so it needs the token. /ui above deliberately does not: it is
                // just the app's own HTML, and the WebView loads it before any
                // token could be handed over.
                if (!TokenOk(headers, query))
                {
                    await WriteAsync(stream, 401, "application/json",
                        Encoding.UTF8.GetBytes("{\"error\":\"missing or bad token\"}"));
                    return;
                }

                if (path == "/status")
                {
                    await WriteAsync(stream, 200, "application/json", Encoding.UTF8.GetBytes(_status()));
                    return;
                }

                if (path == "/frame")
                {
                    // Default to 1280px wide: full 1080p JPEGs are needlessly
                    // expensive for a vision model, and detail beyond this
                    // rarely changes the answer.
                    var w = ParseInt(query, "w", 1280, 160, 3840);
                    var q = ParseInt(query, "q", 80, 20, 100);
                    var jpeg = await _grabFrame(w, q);
                    if (jpeg is null || jpeg.Length == 0)
                    {
                        await WriteAsync(stream, 503, "application/json",
                            Encoding.UTF8.GetBytes("{\"error\":\"no frame - is capture running?\"}"));
                        return;
                    }
                    await WriteAsync(stream, 200, "image/jpeg", jpeg);
                    return;
                }

                await WriteAsync(stream, 404, "application/json",
                    Encoding.UTF8.GetBytes("{\"error\":\"try /status or /frame\"}"));
            }
        }
        catch { /* a broken client must never take the viewer down */ }
    }

    /// <summary>
    /// Returns the request target plus the headers. Headers were previously
    /// thrown away, which is why Host went unchecked - and an unchecked Host on
    /// a loopback service is what makes DNS rebinding work: a page on any site
    /// can resolve its own name to 127.0.0.1 and talk to us. Chrome's
    /// private-network checks stop that today; Firefox and Safari do not.
    /// </summary>
    private static async Task<(string Target, Dictionary<string, string> Headers)?> ReadRequestAsync(NetworkStream s)
    {
        var buf = new byte[8192];
        int n = await s.ReadAsync(buf);
        if (n <= 0) return null;
        var text = Encoding.ASCII.GetString(buf, 0, n);
        var lines = text.Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2 || parts[0] != "GET") return null;

        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
        {
            var c = lines[i].IndexOf(':');
            if (c > 0) h[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }
        return (parts[1], h);
    }

    /// <summary>Host must name loopback and our port, or it is not our client.</summary>
    private bool HostOk(Dictionary<string, string> h)
    {
        if (!h.TryGetValue("Host", out var host)) return false;
        return host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"[::1]:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Constant-time token check for the endpoints that expose the screen.
    /// Accepts a header or a query parameter (curl and MCP both stay easy).
    /// NOTE the honest limit: the token lives in a file readable by this user,
    /// so another process running AS ME can still read it. What this closes is
    /// the browser/rebinding path and anything not running as this user.
    /// </summary>
    private bool TokenOk(Dictionary<string, string> h, Dictionary<string, string> q)
    {
        if (_token.Length == 0) return true;              // not configured: open, as before
        h.TryGetValue("X-SC-Token", out var a);
        q.TryGetValue("k", out var b);
        return Same(a) || Same(b);

        bool Same(string? v) => v is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(v), Encoding.UTF8.GetBytes(_token));
    }

    private static (string, Dictionary<string, string>) SplitQuery(string url)
    {
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = url.IndexOf('?');
        if (i < 0) return (url, q);
        foreach (var kv in url[(i + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var e = kv.Split('=', 2);
            q[e[0]] = e.Length > 1 ? Uri.UnescapeDataString(e[1]) : "";
        }
        return (url[..i], q);
    }

    private static int ParseInt(Dictionary<string, string> q, string key, int fallback, int min, int max)
        => q.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? Math.Clamp(n, min, max) : fallback;

    private static async Task WriteAsync(NetworkStream s, int code, string type, byte[] body)
    {
        var reason = code switch { 200 => "OK", 404 => "Not Found", 503 => "Service Unavailable", _ => "Error" };
        var head = $"HTTP/1.1 {code} {reason}\r\n" +
                   $"Content-Type: {type}\r\n" +
                   $"Content-Length: {body.Length}\r\n" +
                   "Cache-Control: no-store\r\n" +
                   "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head));
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    public void Dispose()
    {
        try { _cts.Cancel(); _listener.Stop(); } catch { }
    }
}
