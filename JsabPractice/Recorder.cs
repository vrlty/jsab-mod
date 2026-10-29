using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Assets.Plugins.lachhh.lachhhEngine.animation;
using System.Threading;
using UnityEngine;

namespace JsabPractice;

// Records every hazard's real hitbox against the song position while a level plays, so the
// editor can replay true motion. Saved to <levels folder>/recordings/<LEVEL>.json, tagged
// with the signature of the level content it was recorded from (LevelImport.SignatureOf).
//
// Format: { format, level, signature, sampleMs, samples: [ { t, p, c: [id,x,y,r,...],
//           q: [id,x1,y1,x2,y2,x3,y3,x4,y4,...] } ] }  in editor coordinates (x 0..1280,
// y 0..720, y up); p = where the pass that recorded the sample started. Each id is one
// tracked shape: one hazard can test many shapes per
// frame in an order that changes (a black hole's spike barrage), so shapes are followed
// frame to frame by predicted position, at full frame rate where matching is reliable.
internal static class Recorder
{
    private const int FormatVersion = 6;
    private const float SampleMs = 1000f / 30f;
    private const float SaveEverySeconds = 20f;
    private const float MaxJumpPerFrame = 90f;
    private const int ForgetAfterFrames = 10;

    private class Track
    {
        public int Id, Frame;
        public float X, Y, Size, Vx, Vy;
    }

    private class Live
    {
        // The actor firing these shapes this frame; pooled actors reuse pointers, so the
        // look is read from it whenever a new track starts rather than once per pointer.
        public Actor Attacker;
        public int Frame = -1;
        public int TrackedFrame = -1;
        public readonly List<float> Circles = new();
        public readonly List<float> Quads = new();
        public readonly List<Track> CircleTracks = new();
        public readonly List<Track> QuadTracks = new();
        public int[] CircleIds = Array.Empty<int>();
        public int[] QuadIds = Array.Empty<int>();
    }

    private class Sample
    {
        public float T;
        public float PassStart;
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
    private static readonly object Gate = new();
    private static int _nextId = 1;
    private static Recording _rec;
    private static int _lastBucket = int.MinValue;
    private static float _nextSave;

    // A "pass" is a stretch of uninterrupted forward play. Deaths rewind to a checkpoint,
    // where the game restarts without the hazards that were already in flight, so data
    // from a pass that started earlier is the more faithful one and is never overwritten
    // by a later pass. NaN = not in a pass (rewinding, or waiting for forward motion).
    private static float _passStart = float.NaN;
    private static float _lastPos = float.NegativeInfinity;
    private static float _lastTickTime;
    private const float DeathTrimMs = 300f;

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
        live.Attacker = attacker;
        if (live.Frame != Time.frameCount)
        {
            live.Frame = Time.frameCount;
            live.Circles.Clear();
            live.Quads.Clear();
        }
        return live;
    }

    // Which Flash animations each hazard type uses, as seen in play: keyed by hazard type, or
    // by class for sub-pieces with no spawn of their own (a line's individual spikes). The
    // sprite capture uses this to know what to render for a level.
    private static readonly SortedDictionary<string, SortedSet<string>> Visuals = new();
    private static bool _visualsDirty;

    // Returns the animation the hazard draws with: its render component's, or the one its
    // spawn names by parameter. Null when unknown.
    private static string NoteVisual(Actor attacker)
    {
        try
        {
            if (attacker == null) return null;
            var meta = attacker.TryCast<Spawn>()?.metaSpawn;
            bool hasMeta = meta != null && !meta.isNull();
            string anim = attacker.renderComponent?.animView?.modelAnim?.id;
            if (string.IsNullOrEmpty(anim))
                anim = AnimFromOwnField(attacker);
            if (string.IsNullOrEmpty(anim) && hasMeta)
            {
                var group = meta.metaParams;
                foreach (string key in new[] { "visual", "customChoice" })
                {
                    if (group != null && group.hasParamStr(key)) { anim = group.getStringStr(key); break; }
                }
            }

            string cls = attacker.GetIl2CppType().Name;
            if (string.IsNullOrEmpty(anim))
                anim = AnimFromClassName(cls);
            if (string.IsNullOrEmpty(anim))
            {
                if (UnmatchedClasses.Add(cls))
                    Plugin.Logger.LogInfo($"[recorder] no art found for hazard class {cls}");
                return null;
            }

            string type = hasMeta ? meta.modelSpawn?.id : null;
            type ??= cls;
            if (!Visuals.TryGetValue(type, out var set))
                Visuals[type] = set = new SortedSet<string>();
            if (set.Add(anim)) _visualsDirty = true;
            return anim;
        }
        catch
        {
            // Purely informational; never let it disturb recording.
            return null;
        }
    }

    public static List<string> VisualsSeen() => Visuals.Values.SelectMany(s => s).Distinct().ToList();

    // Many hazard classes keep their look in a field of their own (RoundSpikes.modelFlashAnim,
    // and ~50 classes with modelAnim) rather than in their render component. Find that field
    // on the actor's real class through the interop types, cached per class.
    private static readonly string[] AnimFieldNames = { "modelFlashAnim", "modelAnim", "animId" };
    private static readonly Dictionary<string, (Type type, PropertyInfo prop)> AnimFields = new();

    // Last resort: the game names art after its classes (BlackHole -> ENEMY_BLACK_HOLE,
    // RoundSpikes -> ENEMY_ROUND_SPIKES, ExplodingSpikes -> ENEMY_EXPLODING_SPIKE).
    private static readonly Dictionary<string, string> ArtByClass = new();
    private static readonly HashSet<string> UnmatchedClasses = new();

    private static string AnimFromClassName(string cls)
    {
        if (ArtByClass.TryGetValue(cls, out var cached))
            return cached;

        string snake = Regex.Replace(cls, "(?<=[a-z0-9])([A-Z])", "_$1").ToUpperInvariant();
        string found = null;
        foreach (string candidate in new[] { "ENEMY_" + snake, "ENEMY_" + snake.TrimEnd('S'), "ENEMY_" + snake + "S", snake })
        {
            var model = ModelFlashAnimationEnum.getFromId(candidate);
            if (model != null && !model.isNull) { found = candidate; break; }
        }
        ArtByClass[cls] = found;
        return found;
    }

    private static string AnimFromOwnField(Actor actor)
    {
        string cls = actor.GetIl2CppType().FullName;
        if (!AnimFields.TryGetValue(cls, out var entry))
        {
            var type = typeof(Spawn).Assembly.GetType(cls);
            var prop = type == null ? null : AnimFieldNames
                .Select(n => type.GetProperty(n, BindingFlags.Public | BindingFlags.Instance))
                .FirstOrDefault(p => p != null && p.PropertyType == typeof(ModelFlashAnimation));
            AnimFields[cls] = entry = (type, prop);
        }
        if (entry.prop == null)
            return null;
        var typed = Activator.CreateInstance(entry.type, actor.Pointer);
        return (entry.prop.GetValue(typed) as ModelFlashAnimation)?.id;
    }

    // Which animation each recorded track belongs to, so the editor can draw it with art.
    private static readonly Dictionary<int, string> TrackVisuals = new();

    private static string VisualsPath(string level) =>
        Path.Combine(SpriteCapture.Dir, LevelDump.FileName(level).Replace(".json", ""), "visuals.json");

    private static void LoadVisuals(string level)
    {
        Visuals.Clear();
        _visualsDirty = false;
        try
        {
            string path = VisualsPath(level);
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.GetProperty("visuals").EnumerateObject())
                Visuals[p.Name] = new SortedSet<string>(p.Value.EnumerateArray().Select(e => e.GetString()));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't read visuals for '{level}': {e.Message}");
        }
    }

    private static void SaveVisuals(string level)
    {
        if (!_visualsDirty) return;
        _visualsDirty = false;
        try
        {
            string path = VisualsPath(level);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var data = new { level, visuals = Visuals.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray()) };
            File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Saving visuals for '{level}' failed: {e.Message}");
        }
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
        if (!EditorBridge.TryReadMusic(music, out float position, out bool playing) || !playing)
        {
            // Keep the clock current through pauses, so a seek made while paused (editor
            // preview) is seen as a jump rather than as time that passed.
            _lastTickTime = Time.unscaledTime;
            _lastBucket = int.MinValue;
            return;
        }

        foreach (var live in LiveShapes.Values)
        {
            if (live.Frame == Time.frameCount && live.TrackedFrame != Time.frameCount)
            {
                live.TrackedFrame = Time.frameCount;
                string visual = null;
                bool looked = false;
                string Visual()
                {
                    if (!looked) { looked = true; visual = NoteVisual(live.Attacker); }
                    return visual;
                }
                live.CircleIds = AssignTracks(live.Circles, 3, live.CircleTracks, Visual);
                live.QuadIds = AssignTracks(live.Quads, 8, live.QuadTracks, Visual);
            }
        }

        // Fast-forwarded stretches (editor previews) aren't normal play.
        if (NoHit.IsFastForwarding)
        {
            _passStart = float.NaN;
            _lastPos = position;
            return;
        }

        if (!InForwardPass(arcade, position))
            return;

        int bucket = (int)(position / SampleMs);
        if (bucket != _lastBucket)
        {
            _lastBucket = bucket;
            var sample = BuildSample(position);
            sample.PassStart = _passStart;
            lock (Gate)
            {
                if (!_rec.Samples.TryGetValue(bucket, out var existing) || _passStart <= existing.PassStart + 1f)
                {
                    _rec.Samples[bucket] = sample;
                    _rec.Dirty = true;
                }
            }
        }

        if (Time.unscaledTime >= _nextSave)
        {
            _nextSave = Time.unscaledTime + SaveEverySeconds;
            Save(background: true);
        }
    }

    // Only record while the song moves forward at playing speed. Backward motion (the rewind
    // after a death) ends the pass; a jump (seek, restart, finished rewind) starts a new one
    // where the music resumes.
    private static bool InForwardPass(ActorArcadeLevelLogic arcade, float pos)
    {
        float now = Time.unscaledTime;
        float elapsedMs = (now - _lastTickTime) * 1000f;
        _lastTickTime = now;

        bool rewinding;
        try { rewinding = arcade.logicRewindMusic?.isRewinding() ?? false; }
        catch { rewinding = false; }

        float last = _lastPos;
        _lastPos = pos;
        if (rewinding || pos < last - 1f)
        {
            _passStart = float.NaN;
            return false;
        }

        bool jumped = pos - last > elapsedMs * 1.5f + 200f;
        if (float.IsNaN(_passStart) || jumped)
        {
            _passStart = pos;
            _lastBucket = int.MinValue;
        }
        return true;
    }

    // The last moments before a death (hazards clearing, death effects) aren't normal
    // gameplay; drop them and end the pass.
    public static void OnPlayerDied()
    {
        if (_rec == null || float.IsNaN(_passStart))
            return;
        float cutoff = _lastPos - DeathTrimMs;
        lock (Gate)
        {
            var doomed = _rec.Samples.Where(kv => kv.Value.PassStart == _passStart && kv.Value.T > cutoff).Select(kv => kv.Key).ToList();
            foreach (int bucket in doomed)
                _rec.Samples.Remove(bucket);
            if (doomed.Count > 0) _rec.Dirty = true;
        }
        _passStart = float.NaN;
    }

    // Match this frame's shapes to the hazard's tracks from previous frames: greedy on the
    // distance to where each track should be by now, among tracks of similar size (sizes
    // may grow, e.g. black holes). Unmatched shapes start new tracks.
    private static int[] AssignTracks(List<float> data, int stride, List<Track> tracks, Func<string> visualOf)
    {
        int frame = Time.frameCount;
        int n = data.Count / stride;
        var ids = new int[n];
        var centers = new (float x, float y, float size)[n];
        for (int s = 0; s < n; s++)
            centers[s] = Center(data, s * stride, stride);

        var pairs = new List<(float cost, int s, int t)>();
        for (int s = 0; s < n; s++)
        {
            var (x, y, size) = centers[s];
            for (int t = 0; t < tracks.Count; t++)
            {
                var tr = tracks[t];
                int df = frame - tr.Frame;
                if (df <= 0 || df > ForgetAfterFrames) continue;
                if (Math.Abs(size - tr.Size) > Math.Max(4f, tr.Size * 0.35f)) continue;
                float px = tr.X + tr.Vx * df, py = tr.Y + tr.Vy * df;
                float d = MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
                if (d <= MaxJumpPerFrame * df) pairs.Add((d, s, t));
            }
        }
        pairs.Sort((a, b) => a.cost.CompareTo(b.cost));

        var shapeDone = new bool[n];
        var trackDone = new bool[tracks.Count];
        foreach (var (_, s, t) in pairs)
        {
            if (shapeDone[s] || trackDone[t]) continue;
            shapeDone[s] = trackDone[t] = true;
            var tr = tracks[t];
            var (x, y, size) = centers[s];
            int df = frame - tr.Frame;
            tr.Vx = (x - tr.X) / df;
            tr.Vy = (y - tr.Y) / df;
            tr.X = x; tr.Y = y; tr.Size = size; tr.Frame = frame;
            ids[s] = tr.Id;
        }

        for (int s = 0; s < n; s++)
        {
            if (shapeDone[s]) continue;
            var (x, y, size) = centers[s];
            var tr = new Track { Id = _nextId++, Frame = frame, X = x, Y = y, Size = size };
            tracks.Add(tr);
            ids[s] = tr.Id;
            string visual = visualOf();
            if (visual != null)
                lock (Gate) TrackVisuals[tr.Id] = visual;
        }

        tracks.RemoveAll(tr => frame - tr.Frame > ForgetAfterFrames);
        return ids;
    }

    private static (float x, float y, float size) Center(List<float> data, int i, int stride)
    {
        if (stride == 3)
            return (data[i], data[i + 1], data[i + 2]);
        float cx = (data[i] + data[i + 2] + data[i + 4] + data[i + 6]) / 4f;
        float cy = (data[i + 1] + data[i + 3] + data[i + 5] + data[i + 7]) / 4f;
        float dx = data[i] - cx, dy = data[i + 1] - cy;
        return (cx, cy, MathF.Sqrt(dx * dx + dy * dy));
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
            for (int s = 0; s * 3 + 2 < live.Circles.Count && s < live.CircleIds.Length; s++)
            {
                circles.Add(live.CircleIds[s]);
                for (int k = 0; k < 3; k++) circles.Add(Mathf.RoundToInt(live.Circles[s * 3 + k]));
            }
            for (int s = 0; s * 8 + 7 < live.Quads.Count && s < live.QuadIds.Length; s++)
            {
                quads.Add(live.QuadIds[s]);
                for (int k = 0; k < 8; k++) quads.Add(Mathf.RoundToInt(live.Quads[s * 8 + k]));
            }
        }
        foreach (var ptr in stale)
            LiveShapes.Remove(ptr);
        return new Sample { T = t, C = circles.ToArray(), Q = quads.ToArray() };
    }

    private static void Begin(string level, string signature)
    {
        _rec = new Recording { Level = level, Signature = signature };
        _lastBucket = int.MinValue;
        _nextSave = Time.unscaledTime + SaveEverySeconds;
        _passStart = float.NaN;
        _lastPos = float.NegativeInfinity;
        _lastTickTime = Time.unscaledTime;
        LiveShapes.Clear();
        lock (Gate) TrackVisuals.Clear();
        LoadVisuals(level);

        // Continue an earlier recording of the same level content.
        string path = PathFor(level);
        try
        {
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("signature").GetString() != signature) return;
            if (!root.TryGetProperty("format", out var format) || format.GetInt32() != FormatVersion) return;
            foreach (var s in root.GetProperty("samples").EnumerateArray())
            {
                float t = s.GetProperty("t").GetSingle();
                _rec.Samples[(int)(t / SampleMs)] = new Sample
                {
                    T = t,
                    PassStart = s.TryGetProperty("p", out var p) ? p.GetSingle() : 0f,
                    C = s.GetProperty("c").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                    Q = s.GetProperty("q").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                };
            }
            if (root.TryGetProperty("tracks", out var tracks))
                lock (Gate)
                    foreach (var t in tracks.EnumerateObject())
                        TrackVisuals[int.Parse(t.Name)] = t.Value.GetString();
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
    }

    // Snapshot under the lock (samples are immutable once recorded), write off the main
    // thread when asked so gameplay doesn't hitch.
    public static void Save(bool background)
    {
        var rec = _rec;
        if (rec == null)
            return;

        SaveVisuals(rec.Level);

        List<Sample> samples;
        Dictionary<int, string> trackVisuals;
        lock (Gate)
        {
            if (!rec.Dirty) return;
            rec.Dirty = false;
            samples = rec.Samples.Values.ToList();
            // Only tracks that are still in the samples.
            var ids = new HashSet<int>();
            foreach (var s in samples)
            {
                for (int i = 0; i < s.C.Length; i += 4) ids.Add(s.C[i]);
                for (int i = 0; i < s.Q.Length; i += 9) ids.Add(s.Q[i]);
            }
            trackVisuals = TrackVisuals.Where(kv => ids.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
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
                    // track id -> the animation that hazard draws with
                    w.WriteStartObject("tracks");
                    foreach (var (id, visual) in trackVisuals)
                        w.WriteString(id.ToString(), visual);
                    w.WriteEndObject();
                    w.WriteStartArray("samples");
                    foreach (var s in samples)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("t", MathF.Round(s.T, 1));
                        w.WriteNumber("p", MathF.Round(s.PassStart));
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
