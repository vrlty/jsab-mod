using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace JsabPractice;

// Local HTTP endpoint for the browser level editor, bound to 127.0.0.1 only.
//   GET  /status   -> which level is playing and where the music is
//   POST /preview  {"level":"MUSIC_FOX","timeMs":12345} -> reload the custom file, jump there
//   GET  /display?depth=N -> the Flash display list's hazard layers (diagnostics)
//   POST /autorecord {only, difficulty, speed, redo} / POST /autorecord/stop (AutoRecorder)
// Requests are answered on a worker thread but everything touching the game runs on
// Unity's main thread via Pump().
internal static class EditorBridge
{
    private static readonly ConcurrentQueue<Action> MainThread = new();
    private static TcpListener _listener;

    public static void Start(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            new Thread(AcceptLoop) { IsBackground = true, Name = "JsabEditorBridge" }.Start();
            Plugin.Logger.LogInfo($"Editor bridge listening on http://127.0.0.1:{port}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Editor bridge couldn't start on port {port}: {e.Message}");
        }
    }

    // Called every frame from PracticeRunner.Update.
    public static void Pump()
    {
        while (MainThread.TryDequeue(out var action))
            action();
    }

    private static void AcceptLoop()
    {
        while (true)
        {
            TcpClient client;
            try { client = _listener.AcceptTcpClient(); }
            catch { return; }
            ThreadPool.QueueUserWorkItem(_ => Handle(client));
        }
    }

    private static void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var (method, path, body) = ReadRequest(stream);
                int code;
                string json;
                try
                {
                    (code, json) = Route(method, path, body);
                }
                catch (Exception e)
                {
                    (code, json) = (500, Error(e.InnerException?.Message ?? e.Message));
                }
                WriteResponse(stream, code, json);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Editor bridge request failed: {e.Message}");
            }
        }
    }

    private static (int, string) Route(string method, string path, string body)
    {
        if (method == "OPTIONS")
            return (204, null);

        int q = path.IndexOf('?');
        string query = q >= 0 ? path.Substring(q + 1) : "";
        if (q >= 0) path = path.Substring(0, q);

        return (method, path) switch
        {
            ("GET", "/status") => (200, OnMain(Status)),
            ("POST", "/preview") => Preview(body),
            ("POST", "/autorecord") => (200, OnMain(() => AutoRecorder.Start(body))),
            ("POST", "/autorecord/stop") => (200, OnMain(AutoRecorder.Stop)),
            ("GET", "/songs") => (200, OnMain(AutoRecorder.Songs)),
            ("GET", "/display") => (200, OnMain(() => DisplayList.Dump(QueryInt(query, "depth", 1)))),
            ("GET", "/il2cpp/layout") => (200, OnMain(LayoutDump.Dump)),
            ("GET", "/il2cpp/usages") => (200, OnMain(LayoutDump.Usages)),
            _ => (404, Error($"No such endpoint: {method} {path}")),
        };
    }

    private static string Status()
    {
        var arcade = NoHit.GetArcadeLevel(quiet: true);
        if (arcade == null)
            return JsonSerializer.Serialize(new { ok = true, version = Plugin.Version, inLevel = false, autoRecord = AutoRecorder.Progress });

        var song = arcade.currentSong;
        var music = arcade.logicEnemyCreator?.sfxView;
        TryReadMusic(music, out float timeMs, out bool playing);
        // While paused the music reports 0; the pause logic remembers where it stopped.
        if (!playing && timeMs <= 0f)
        {
            try { timeMs = arcade.logicUIPause?.positionBeforePause ?? 0f; }
            catch { /* keep 0 */ }
        }
        float lengthMs = 0f;
        try { if (music != null) lengthMs = music.length * 1000f; }
        catch { /* no clip loaded */ }

        return JsonSerializer.Serialize(new
        {
            ok = true,
            version = Plugin.Version,
            inLevel = song != null,
            level = song == null ? null : LevelDump.LevelName(song),
            song = song?.name,
            timeMs,
            lengthMs,
            playing,
            frozen = NoHit.IsFrozen,
            previewHeld = NoHit.IsPreviewPaused,
            fastForwarding = NoHit.IsFastForwarding,
            autoRecord = AutoRecorder.Progress,
        });
    }

    // TimelineComponent.TIME_OFFSET: the game adds this static to the music clip's position for
    // every timeline (getJukeboxPosition), so it is part of the time spawns are made by. It was
    // 0 in every run looked at (2026-11-12).
    public static float TimelineOffsetMs()
    {
        try { return TimelineComponent.TIME_OFFSET; }
        catch { return 0f; }
    }

    // DynamicMusic throws while no clip is loaded (between songs, during loading).
    // Its position is in ms, as the level's timelines see it (plus TIME_OFFSET); its length is
    // the clip's, in seconds.
    public static bool TryReadMusic(DynamicMusic music, out float positionMs, out bool playing)
    {
        positionMs = 0f;
        playing = false;
        if (music == null)
            return false;
        try
        {
            positionMs = music.position + TimelineOffsetMs();
            playing = music.isPlaying;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static (int, string) Preview(string body)
    {
        string level;
        float timeMs;
        NoHit.FastForwardOptions ff = null;
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
        {
            var root = doc.RootElement;
            level = root.TryGetProperty("level", out var l) ? l.GetString() : null;
            timeMs = root.TryGetProperty("timeMs", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetSingle() : 0f;
            // Optional overrides for how the preview fast-forwards (used while tuning it).
            if (root.TryGetProperty("ff", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                var d = NoHit.FastForward;
                ff = new NoHit.FastForwardOptions
                {
                    Speed = f.TryGetProperty("speed", out var v) ? v.GetSingle() : d.Speed,
                    Music = f.TryGetProperty("music", out v) ? v.GetBoolean() : d.Music,
                    GameSpeed = f.TryGetProperty("gameSpeed", out v) ? v.GetBoolean() : d.GameSpeed,
                    TimeScale = f.TryGetProperty("timeScale", out v) ? v.GetBoolean() : d.TimeScale,
                    LingerMs = f.TryGetProperty("lingerMs", out v) ? v.GetSingle() : d.LingerMs,
                    MaxLookbackMs = f.TryGetProperty("maxLookbackMs", out v) ? v.GetSingle() : d.MaxLookbackMs,
                };
            }
        }

        string result = OnMain(() =>
        {
            var arcade = NoHit.GetArcadeLevel();
            var song = arcade?.currentSong;
            if (song == null)
                return Error("The game isn't in a practice level. Start the song in the game first.");

            string playing = LevelDump.LevelName(song);
            if (level != null && !string.Equals(playing, level, StringComparison.OrdinalIgnoreCase))
                return Error($"The game is playing {playing}, not {level}. Start {level} in the game first.");

            return NoHit.PreviewFrom(arcade, timeMs, ff);
        });
        return (result.Contains("\"error\"") ? 409 : 200, result);
    }

    private static string OnMain(Func<string> fn)
    {
        var tcs = new TaskCompletionSource<string>();
        MainThread.Enqueue(() =>
        {
            try { tcs.SetResult(fn()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        if (!tcs.Task.Wait(4000))
            throw new TimeoutException("The game didn't respond (is it loading or minimised?)");
        return tcs.Task.Result;
    }

    private static int QueryInt(string query, string key, int fallback)
    {
        foreach (string part in query.Split('&'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part.Substring(0, eq) == key && int.TryParse(part.Substring(eq + 1), out int v))
                return v;
        }
        return fallback;
    }

    public static string Error(string message) => JsonSerializer.Serialize(new { ok = false, error = message });

    private static (string method, string path, string body) ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0)
        {
            int n = stream.Read(chunk, 0, chunk.Length);
            if (n <= 0) throw new IOException("connection closed before the request was complete");
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
            if (buffer.Length > 64 * 1024) throw new IOException("request headers too large");
        }

        byte[] data = buffer.GetBuffer();
        string head = Encoding.ASCII.GetString(data, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] requestLine = lines[0].Split(' ');
        int contentLength = 0;
        foreach (string line in lines)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                int.TryParse(line.Substring(15).Trim(), out contentLength);
        }
        if (contentLength > 32 * 1024 * 1024) throw new IOException("request body too large");

        int bodyStart = headerEnd + 4;
        var body = new MemoryStream();
        body.Write(data, bodyStart, (int)buffer.Length - bodyStart);
        while (body.Length < contentLength)
        {
            int n = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, contentLength - body.Length));
            if (n <= 0) break;
            body.Write(chunk, 0, n);
        }
        return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
    {
        int i = new ReadOnlySpan<byte>(data, 0, length).IndexOf(pattern);
        return i;
    }

    private static void WriteResponse(NetworkStream stream, int code, string json)
    {
        byte[] body = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        string reason = code switch { 200 => "OK", 204 => "No Content", 404 => "Not Found", 409 => "Conflict", _ => "Error" };
        // The editor runs from file://, so allow any origin (the socket only listens on loopback).
        string head =
            $"HTTP/1.1 {code} {reason}\r\n" +
            "Access-Control-Allow-Origin: *\r\n" +
            "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
            "Access-Control-Allow-Headers: Content-Type\r\n" +
            "Access-Control-Allow-Private-Network: true\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";
        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        stream.Write(headBytes, 0, headBytes.Length);
        stream.Write(body, 0, body.Length);
    }
}
