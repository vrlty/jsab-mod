using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using UnityEngine;

namespace JsabPractice;

// Records every hazard's real hitbox against the song position while a level plays, so the
// editor can replay true motion. Saved to <levels folder>/recordings/<LEVEL>.json, tagged
// with the signature of the level content it was recorded from (LevelImport.SignatureOf).
//
// Format: { format, level, signature, sampleMs, samples: [ { t, c: [id,x,y,r,...],
//           q: [id,x1,y1,x2,y2,x3,y3,x4,y4,...] } ] }  in editor coordinates (x 0..1280,
// y 0..720, y up). ids are stable per hazard actor within a recording.
internal static class Recorder
{
    private const int FormatVersion = 1;
    private const float SampleMs = 50f;
    private const float SaveEverySeconds = 20f;

    private class Live
    {
        public int Frame = -1;
        public readonly List<float> Circles = new();
        public readonly List<float> Quads = new();
    }

    private class Sample
    {
        public float T;
        public int[] C;
        public int[] Q;
    }

    private class Recording
    {
        public string Level;
        public string Signature;
        public readonly SortedDictionary<int, Sample> Samples = new();
        public bool Dirty;
    }

    private static readonly Dictionary<IntPtr, Live> LiveShapes = new();
    private static readonly Dictionary<IntPtr, int> Ids = new();
    private static readonly object Gate = new();
    private static int _nextId = 1;
    private static Recording _rec;
    private static int _lastBucket = int.MinValue;
    private static float _nextSave;

    public static string Dir => Path.Combine(LevelDump.RootDir, "recordings");

    public static void OnCircle(Actor attacker, float x, float y, float r)
    {
        var live = LiveFor(attacker);
        if (live == null) return;
        live.Circles.Add(x + 640f);
        live.Circles.Add(y + 360f);
        live.Circles.Add(r);
    }

    public static void OnRect(Actor attacker, float x, float y, float scaleX, float scaleY, float rot)
    {
        if (_rec == null || attacker == null) return;
        Hitboxes.RectCorners(x, y, scaleX, scaleY, rot, out var a, out var b, out var c, out var d);
        OnQuad(attacker, a, b, c, d);
    }

    public static void OnQuad(Actor attacker, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        var live = LiveFor(attacker);
        if (live == null) return;
        foreach (var p in new[] { a, b, c, d })
        {
            live.Quads.Add(p.x + 640f);
            live.Quads.Add(p.y + 360f);
        }
    }

    // A hazard may test several shapes per frame; keep all of this frame's, drop older ones.
    private static Live LiveFor(Actor attacker)
    {
        if (_rec == null || attacker == null)
            return null;
        IntPtr ptr = attacker.Pointer;
        if (!LiveShapes.TryGetValue(ptr, out var live))
            LiveShapes[ptr] = live = new Live();
        if (live.Frame != Time.frameCount)
        {
            live.Frame = Time.frameCount;
            live.Circles.Clear();
            live.Quads.Clear();
        }
        return live;
    }

    // Called from LateUpdate, after the game has run this frame's collision tests.
    public static void Tick()
    {
        if (!Plugin.RecordMotion.Value)
        {
            End();
            return;
        }

        var arcade = NoHit.GetArcadeLevel(quiet: true);
        var song = arcade?.currentSong;
        if (song == null)
        {
            End();
            return;
        }

        string level = LevelDump.LevelName(song);
        string signature = LevelImport.SignatureOf(level);
        if (_rec == null || _rec.Level != level || _rec.Signature != signature)
        {
            End();
            Begin(level, signature);
        }

        var music = arcade.logicEnemyCreator?.sfxView;
        if (music == null || !music.isPlaying)
        {
            _lastBucket = int.MinValue;
            return;
        }

        int bucket = (int)(music.position / SampleMs);
        if (bucket != _lastBucket)
        {
            _lastBucket = bucket;
            var sample = BuildSample(music.position);
            lock (Gate)
            {
                _rec.Samples[bucket] = sample;
                _rec.Dirty = true;
            }
        }

        if (Time.unscaledTime >= _nextSave)
        {
            _nextSave = Time.unscaledTime + SaveEverySeconds;
            Save(background: true);
        }
    }

    private static Sample BuildSample(float t)
    {
        // Include shapes from the last couple of frames: not every hazard hit-tests every frame.
        int oldest = Time.frameCount - 2;
        var circles = new List<int>();
        var quads = new List<int>();
        var stale = new List<IntPtr>();
        foreach (var (ptr, live) in LiveShapes)
        {
            if (live.Frame < oldest)
            {
                if (live.Frame < Time.frameCount - 120) stale.Add(ptr);
                continue;
            }
            int id = IdFor(ptr);
            for (int i = 0; i + 2 < live.Circles.Count; i += 3)
            {
                circles.Add(id);
                circles.Add(Mathf.RoundToInt(live.Circles[i]));
                circles.Add(Mathf.RoundToInt(live.Circles[i + 1]));
                circles.Add(Mathf.RoundToInt(live.Circles[i + 2]));
            }
            for (int i = 0; i + 7 < live.Quads.Count; i += 8)
            {
                quads.Add(id);
                for (int k = 0; k < 8; k++) quads.Add(Mathf.RoundToInt(live.Quads[i + k]));
            }
        }
        foreach (var ptr in stale)
        {
            LiveShapes.Remove(ptr);
            Ids.Remove(ptr);
        }
        return new Sample { T = t, C = circles.ToArray(), Q = quads.ToArray() };
    }

    // Pooled actors reuse pointers, so a pointer that went quiet gets a fresh id next time.
    private static int IdFor(IntPtr ptr)
    {
        if (!Ids.TryGetValue(ptr, out int id))
            Ids[ptr] = id = _nextId++;
        return id;
    }

    private static void Begin(string level, string signature)
    {
        _rec = new Recording { Level = level, Signature = signature };
        _lastBucket = int.MinValue;
        _nextSave = Time.unscaledTime + SaveEverySeconds;
        LiveShapes.Clear();
        Ids.Clear();

        // Continue an earlier recording of the same level content.
        string path = PathFor(level);
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("signature").GetString() != signature) return;
            foreach (var s in root.GetProperty("samples").EnumerateArray())
            {
                float t = s.GetProperty("t").GetSingle();
                _rec.Samples[(int)(t / SampleMs)] = new Sample
                {
                    T = t,
                    C = s.GetProperty("c").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                    Q = s.GetProperty("q").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                };
            }
            _nextId = 1 + _rec.Samples.Values.SelectMany(IdsIn).DefaultIfEmpty(0).Max();
            Plugin.Logger.LogInfo($"Continuing motion recording for '{level}' ({_rec.Samples.Count} samples)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read the existing recording for '{level}', starting fresh: {e.Message}");
            _rec.Samples.Clear();
        }

        static IEnumerable<int> IdsIn(Sample s)
        {
            for (int i = 0; i < s.C.Length; i += 4) yield return s.C[i];
            for (int i = 0; i < s.Q.Length; i += 9) yield return s.Q[i];
        }
    }

    public static void End()
    {
        if (_rec == null)
            return;
        Save(background: false);
        _rec = null;
        LiveShapes.Clear();
        Ids.Clear();
    }

    // Snapshot under the lock (samples are immutable once recorded), write off the main
    // thread when asked so gameplay doesn't hitch.
    public static void Save(bool background)
    {
        var rec = _rec;
        if (rec == null)
            return;

        List<Sample> samples;
        lock (Gate)
        {
            if (!rec.Dirty) return;
            rec.Dirty = false;
            samples = rec.Samples.Values.ToList();
        }

        void Write()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string path = PathFor(rec.Level), tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                using (var stream = File.Create(tmp))
                using (var w = new Utf8JsonWriter(stream))
                {
                    w.WriteStartObject();
                    w.WriteNumber("format", FormatVersion);
                    w.WriteString("level", rec.Level);
                    w.WriteString("signature", rec.Signature);
                    w.WriteNumber("sampleMs", SampleMs);
                    w.WriteStartArray("samples");
                    foreach (var s in samples)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("t", MathF.Round(s.T, 1));
                        WriteInts(w, "c", s.C);
                        WriteInts(w, "q", s.Q);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                File.Move(tmp, path, overwrite: true);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Saving the motion recording for '{rec.Level}' failed: {e.Message}");
            }
        }

        if (background) ThreadPool.QueueUserWorkItem(_ => Write());
        else Write();
    }

    private static void WriteInts(Utf8JsonWriter w, string name, int[] values)
    {
        w.WriteStartArray(name);
        foreach (int v in values) w.WriteNumberValue(v);
        w.WriteEndArray();
    }

    private static string PathFor(string level) => Path.Combine(Dir, LevelDump.FileName(level));
}
