using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Kuroko;

/// <summary>
/// MCP server, spoken over stdio, hosted by this same exe (`Kuroko.exe --mcp`).
///
/// This replaces the standalone Python server so the app has no external
/// interpreter dependency. It does NOT capture anything itself: the dongle is
/// exclusive and the running viewer owns it, so this proxies to the viewer's
/// loopback API and to the Steam Deck's gamepad agent.
///
/// A WinExe has no console of its own, but an MCP client always redirects the
/// child's stdin/stdout - so the standard streams are valid here even though
/// nothing is attached when double-clicked.
/// </summary>
internal static class McpServer
{
    private static string Api =>
        (Environment.GetEnvironmentVariable("KUROKO_API") ?? "http://127.0.0.1:8791").TrimEnd('/');
    private static string Pad =>
        (Environment.GetEnvironmentVariable("KUROKO_PAD") ?? "").TrimEnd('/');
    private static string PadToken =>
        Environment.GetEnvironmentVariable("KUROKO_PAD_TOKEN") ?? "";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private const string ApiHint =
        "Could not reach the Kuroko viewer. Start Kuroko.exe - it must be running and " +
        "capturing, because it owns the capture device exclusively.";
    private const string PadHint =
        "No controller configured. Run deckpad.py on the Steam Deck and set KUROKO_PAD " +
        "(e.g. http://100.127.125.103:8792) plus KUROKO_PAD_TOKEN (see ~/.deckpad_token).";

    internal static async Task<int> RunAsync()
    {
        // Explicit UTF-8 with no BOM: a BOM on the first line is not valid JSON
        // to the client, and would break the handshake before it starts.
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };

        string? line;
        while ((line = await stdin.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch { continue; }
            if (msg is null) continue;

            var method = msg["method"]?.GetValue<string>();
            var id = msg["id"];

            try
            {
                switch (method)
                {
                    case "initialize":
                        Respond(stdout, id, new JsonObject
                        {
                            ["protocolVersion"] = "2024-11-05",
                            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                            ["serverInfo"] = new JsonObject
                            {
                                ["name"] = "kuroko",
                                ["version"] = "2.0.0",
                            },
                        });
                        break;

                    case "tools/list":
                        Respond(stdout, id, new JsonObject { ["tools"] = Tools() });
                        break;

                    case "tools/call":
                    {
                        var p = msg["params"];
                        var name = p?["name"]?.GetValue<string>() ?? "";
                        var args = p?["arguments"] as JsonObject ?? new JsonObject();
                        try
                        {
                            var content = await CallAsync(name, args);
                            Respond(stdout, id, new JsonObject { ["content"] = content });
                        }
                        catch (Exception ex)
                        {
                            Respond(stdout, id, new JsonObject
                            {
                                ["content"] = new JsonArray(Text(ex.Message)),
                                ["isError"] = true,
                            });
                        }
                        break;
                    }

                    default:
                        // Notifications carry no id and need no reply.
                        if (id is not null)
                            RespondError(stdout, id, -32601, $"method not found: {method}");
                        break;
                }
            }
            catch (Exception ex)
            {
                if (id is not null) RespondError(stdout, id, -32603, ex.Message);
            }
        }
        return 0;
    }

    private static JsonObject Text(string s) => new() { ["type"] = "text", ["text"] = s };

    private static JsonObject Image(byte[] jpeg) => new()
    {
        ["type"] = "image",
        ["data"] = Convert.ToBase64String(jpeg),
        ["mimeType"] = "image/jpeg",
    };

    private static void Respond(TextWriter o, JsonNode? id, JsonNode result)
    {
        var m = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };
        o.WriteLine(m.ToJsonString());
    }

    private static void RespondError(TextWriter o, JsonNode? id, int code, string message)
    {
        var m = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        };
        o.WriteLine(m.ToJsonString());
    }

    /// <summary>
    /// Read the viewer's per-launch API token each time rather than caching it:
    /// the viewer regenerates it on restart, and an MCP server outlives several
    /// viewer sessions.
    /// </summary>
    private static HttpRequestMessage ApiReq(string pathAndQuery)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, Api + pathAndQuery);
        var t = ApiToken.Read();
        if (t.Length > 0) m.Headers.Add("X-SC-Token", t);
        return m;
    }

    private static async Task<byte[]> FrameAsync(int width, int quality)
    {
        try
        {
            var r = await Http.SendAsync(ApiReq($"/frame?w={width}&q={quality}"));
            var bytes = await r.Content.ReadAsByteArrayAsync();
            if (!r.IsSuccessStatusCode || r.Content.Headers.ContentType?.MediaType != "image/jpeg")
                throw new Exception(Encoding.UTF8.GetString(bytes));
            return bytes;
        }
        catch (HttpRequestException) { throw new Exception(ApiHint); }
    }

    private static async Task<string> ApiGetAsync(string path)
    {
        try
        {
            var r = await Http.SendAsync(ApiReq(path));
            return await r.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException) { throw new Exception(ApiHint); }
    }

    private static async Task<string> PadAsync(string path, JsonObject? body)
    {
        if (string.IsNullOrEmpty(Pad)) throw new Exception(PadHint);
        var req = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, Pad + path);
        // The pad binds 0.0.0.0 so the PC can reach it across the tailnet; the
        // token is what keeps everyone else on that tailnet from driving it.
        if (!string.IsNullOrEmpty(PadToken)) req.Headers.Add("X-Deckpad-Token", PadToken);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        try
        {
            var r = await Http.SendAsync(req);
            var s = await r.Content.ReadAsStringAsync();
            if (!r.IsSuccessStatusCode) throw new Exception($"controller returned {(int)r.StatusCode}: {s}");
            return s;
        }
        catch (HttpRequestException) { throw new Exception(PadHint); }
    }

    private static int Arg(JsonObject a, string k, int dflt, int min, int max)
        => a[k] is JsonNode n && int.TryParse(n.ToString(), out var v) ? Math.Clamp(v, min, max) : dflt;

    private static double ArgD(JsonObject a, string k, double dflt)
        => a[k] is JsonNode n && double.TryParse(n.ToString(), out var v) ? v : dflt;

    private static async Task<JsonArray> CallAsync(string name, JsonObject a)
    {
        switch (name)
        {
            case "get_status":
                return new JsonArray(Text(await ApiGetAsync("/status")));

            case "get_screen":
                return new JsonArray(Image(await FrameAsync(Arg(a, "width", 1280, 160, 3840),
                                                            Arg(a, "quality", 80, 20, 100))));

            case "get_screen_burst":
            {
                var count = Arg(a, "count", 4, 2, 8);
                var gap = Arg(a, "interval_ms", 300, 50, 2000);
                var w = Arg(a, "width", 960, 160, 3840);
                var outp = new JsonArray();
                for (int i = 0; i < count; i++)
                {
                    if (i > 0) await Task.Delay(gap);
                    // Label each frame: without an ordering marker a model
                    // cannot tell which way time runs through the images.
                    outp.Add(Text($"frame {i + 1} of {count} (+{i * gap}ms)"));
                    outp.Add(Image(await FrameAsync(w, 75)));
                }
                return outp;
            }

            case "press_button":
            {
                var btn = a["button"]?.GetValue<string>() ?? throw new Exception("button is required");
                await PadAsync("/press", new JsonObject { ["button"] = btn, ["ms"] = Arg(a, "ms", 80, 10, 5000) });
                return new JsonArray(Text($"pressed {btn}"));
            }

            case "move_stick":
            {
                var dir = a["direction"]?.GetValue<string>();
                var hold = Arg(a, "ms", 0, 0, 10000);
                if (!string.IsNullOrEmpty(dir))
                {
                    // A d-pad press is a hat axis that must be released, or the
                    // menu keeps scrolling forever.
                    await PadAsync("/sequence", new JsonObject
                    {
                        ["steps"] = new JsonArray(
                            new JsonObject { ["type"] = "axis", ["axis"] = dir, ["value"] = 1 },
                            new JsonObject { ["type"] = "wait", ["ms"] = hold == 0 ? 80 : hold },
                            new JsonObject { ["type"] = "axis", ["axis"] = "hatx", ["value"] = 0 },
                            new JsonObject { ["type"] = "axis", ["axis"] = "haty", ["value"] = 0 }),
                    });
                    return new JsonArray(Text($"d-pad {dir}"));
                }

                var stick = (a["stick"]?.GetValue<string>() ?? "left").ToLowerInvariant();
                var steps = new JsonArray();
                if (stick is "lt" or "rt")
                {
                    steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = stick, ["value"] = ArgD(a, "value", 1) });
                    if (hold > 0)
                    {
                        steps.Add(new JsonObject { ["type"] = "wait", ["ms"] = hold });
                        steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = stick, ["value"] = 0 });
                    }
                    await PadAsync("/sequence", new JsonObject { ["steps"] = steps });
                    return new JsonArray(Text($"{stick} -> {ArgD(a, "value", 1)}"));
                }

                var (ax, ay) = stick == "right" ? ("rx", "ry") : ("lx", "ly");
                double x = ArgD(a, "x", 0), y = ArgD(a, "y", 0);
                steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = ax, ["value"] = x });
                steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = ay, ["value"] = y });
                if (hold > 0)
                {
                    steps.Add(new JsonObject { ["type"] = "wait", ["ms"] = hold });
                    steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = ax, ["value"] = 0 });
                    steps.Add(new JsonObject { ["type"] = "axis", ["axis"] = ay, ["value"] = 0 });
                }
                await PadAsync("/sequence", new JsonObject { ["steps"] = steps });
                return new JsonArray(Text($"{stick} stick -> ({x:0.00}, {y:0.00})"
                                          + (hold > 0 ? " then centred" : " (still held)")));
            }

            case "input_sequence":
            {
                var steps = a["steps"] as JsonArray ?? new JsonArray();
                var safe = steps.DeepClone() as JsonArray ?? new JsonArray();
                // press_button clamps ms to 10-5000, but this path forwarded
                // steps verbatim and deckpad does an unbounded int(step["ms"]) -
                // so a single step could hold a PHYSICAL button down for days.
                // Clamp here: every step of every sequence routes through it.
                foreach (var s in safe)
                    if (s is JsonObject o && o["ms"] is JsonNode n && int.TryParse(n.ToString(), out var v))
                        o["ms"] = Math.Clamp(v, 0, 10000);
                await PadAsync("/sequence", new JsonObject { ["steps"] = safe });
                return new JsonArray(Text($"ran {steps.Count} steps"));
            }

            case "release_all":
                await PadAsync("/release_all", new JsonObject());
                return new JsonArray(Text("all inputs released"));

            case "controller_status":
                return new JsonArray(Text(await PadAsync("/status", null)));

            default:
                throw new Exception($"unknown tool: {name}");
        }
    }

    private static JsonArray Tools()
    {
        JsonObject T(string name, string desc, JsonObject props, params string[] required)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
            if (required.Length > 0) schema["required"] = new JsonArray(Array.ConvertAll(required, r => (JsonNode)r!));
            return new JsonObject { ["name"] = name, ["description"] = desc, ["inputSchema"] = schema };
        }
        JsonObject Int(string desc, int min, int max) =>
            new() { ["type"] = "integer", ["description"] = desc, ["minimum"] = min, ["maximum"] = max };
        JsonObject Num(string desc, double min, double max) =>
            new() { ["type"] = "number", ["description"] = desc, ["minimum"] = min, ["maximum"] = max };
        JsonObject Str(string desc) => new() { ["type"] = "string", ["description"] = desc };

        return new JsonArray(
            T("get_screen",
              "Capture what is currently on the console/game screen as an image. Use this to see the "
              + "live game state - menus, HUD, dialogue, what the player is doing right now.",
              new JsonObject
              {
                  ["width"] = Int("Width in px to scale to (default 1280). Smaller is cheaper; use 1920 "
                                  + "only when fine detail like small text matters.", 160, 3840),
                  ["quality"] = Int("JPEG quality 20-100 (default 80).", 20, 100),
              }),

            T("get_screen_burst",
              "Capture several frames in sequence to show MOTION - what is moving, which way, and what "
              + "just happened. A single frame cannot answer that. Use for gameplay analysis, reaction "
              + "timing, or teaching from demonstration.",
              new JsonObject
              {
                  ["count"] = Int("Frames to capture (2-8, default 4).", 2, 8),
                  ["interval_ms"] = Int("Delay between frames in ms (default 300).", 50, 2000),
                  ["width"] = Int("Width per frame (default 960 - bursts are several images).", 160, 3840),
              }),

            T("get_status",
              "Whether capture is live, plus resolution, framerate, device and recording state. "
              + "Check this first if get_screen fails.",
              new JsonObject()),

            T("press_button",
              "Press a controller button on the Steam Deck. Buttons: a, b, x, y, lb, rb, start, back, "
              + "guide, l3, r3. Use get_screen first to see the state, press, then get_screen again to "
              + "see the result.",
              new JsonObject
              {
                  ["button"] = Str("a|b|x|y|lb|rb|start|back|guide|l3|r3"),
                  ["ms"] = Int("How long to hold in ms (default 80). Longer for charged inputs.", 10, 5000),
              }, "button"),

            T("move_stick",
              "Move an analog stick or press the d-pad, then optionally recentre. Sticks take x/y from "
              + "-1..1 (x: -1 left, +1 right; y: -1 up, +1 down). Triggers take 0..1. For the d-pad use "
              + "direction instead.",
              new JsonObject
              {
                  ["stick"] = Str("left|right|lt|rt"),
                  ["x"] = Num("-1..1", -1, 1),
                  ["y"] = Num("-1..1", -1, 1),
                  ["value"] = Num("Trigger amount when stick is lt/rt.", 0, 1),
                  ["direction"] = Str("d-pad: up|down|left|right"),
                  ["ms"] = Int("Hold this long then recentre. 0 (default) leaves it held - remember to "
                               + "recentre or the character keeps walking.", 0, 10000),
              }),

            T("input_sequence",
              "Run several inputs in order, with waits - for combos, menu navigation, or a timed "
              + "manoeuvre. Each step is {type: press|hold|axis|wait, ...}. More reliable than separate "
              + "calls when timing matters, because there is no round-trip between steps.",
              new JsonObject
              {
                  ["steps"] = new JsonObject
                  {
                      ["type"] = "array",
                      ["description"] = "e.g. [{\"type\":\"press\",\"button\":\"a\"},{\"type\":\"wait\",\"ms\":200},"
                                      + "{\"type\":\"axis\",\"axis\":\"lx\",\"value\":-1}]",
                      ["items"] = new JsonObject { ["type"] = "object" },
                  },
              }, "steps"),

            T("release_all",
              "Release every button and recentre every stick. Use after an error, or whenever inputs "
              + "might be stuck - a jammed stick keeps the character moving.",
              new JsonObject()),

            T("controller_status",
              "Whether the virtual gamepad is reachable, and which buttons/axes are currently held. "
              + "Check this if inputs seem to do nothing.",
              new JsonObject()));
    }
}
