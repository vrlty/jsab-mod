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
// editor can replay true motion. Saved to <levels folder>/recordings/<LEVEL>.json (vanilla
// content) or <LEVEL>.custom.json (the custom file), tagged with the signature of the level
// content it was recorded from (LevelImport.SignatureOf).
//
// Format: { format, level, signature, sampleMs, roomSeed, tracks, symbols, placements, samples: [ { t, p,
//           c: [id,x,y,r,...], q: [id,x1,y1,x2,y2,x3,y3,x4,y4,...], v: [visual...],
//           z: [zone...] } ] }
// in editor coordinates (x 0..1280, y 0..720, y down); p = where the pass that recorded the
// sample started. From format 13 a sample also has f and m: the game tick its state is from
// (hazards move in GameScene.update, once per FixedUpdate at 60 Hz, while samples are taken in
// LateUpdate at the render rate) and the music position the level timeline saw on that tick.
// f counts ticks since the game started, so only differences within a pass (same p) mean
// anything. Each c/q id is one tracked collision shape: one hazard can test many
// shapes per frame in an order that changes (a black hole's spike barrage), so shapes are
// followed frame to frame by predicted position, at full frame rate where matching is
// reliable.
//
// v is what the game draws: every visible hazard clip in the Flash display list, as
//   root  = [id, sym, frame, alpha, [a, b, c, d, tx, ty], kid..., {c}?]
//   kid   = [sym, frame, alpha, [a, b, c, d, tx, ty] | 0, kid..., {c}?]
// sym indexes symbols: [[name, bundle], ...] (the exported art, tools/extract_art.py);
// frame is 1-based; transforms are y-down affines (x' = a x + c y + tx, y' = b x + d y + ty)
// taken from the matrices the game draws with, a root's in editor coordinates, a kid's
// relative to its parent (0 = identity); {c: [r, g, b]} is the clip's own tint when not
// neutral (see WriteVisual). From format 12 a root's object can also hold s: [idInSong, type],
// the spawn whose clip it is (MetaSpawn.idInSong: the level's own fixed index, which the
// editor matches its spawns by). id follows one on-screen object from sample to sample. The game drives nested clips itself
// (and stretches timelines and tints to a hazard's timing), so frames and tints are taken
// as they are rather than worked out from the art.
//
// created (format 13): [idInSong, tick, music] per spawn: the game tick the timeline created it
// on (same count as a sample's f) and the music position then. shots: the same for each
// spawn's first shot (ammo its weapon fired, AmmoOwners).
//
// placements: [type, timeMs, x, y, rotation] where each spawn actually landed (random
// positions and sides resolved), in creation order among spawns with the same type and
// timeMs (as written in the level file); rotation is the hazard view's, a wall's tells its side.
//
// z is the warning bands (DangerZoneEffect, drawn by code, not art) as
//   [offsetX, offsetY, flashing, x, y, width, height, x, y, width, height, ...]
// with each rect at offset + (x, y) in editor coordinates.
//
// From format 11 roomSeed is the run's MyMath.roomSeed (see GameRandom): the editor
// reproduces every random choice of the run from it, and placements are only that run's.
//
// From format 10 everything (c, q, v, z, placements) is where it shows on screen: scrolling
// levels (CamMove, boss cameras) move the camera through the world, and hazards spawn
// relative to it, so world coordinates ran off the stage. Camera shake is left out. Each
// sample whose camera isn't at rest has k = [a, b, c, d, tx, ty], the world -> screen affine
// in editor coordinates (screen = k * world), so the editor can scroll its background.
internal static class Recorder
{
    private const int FormatVersion = 13;
    private const float SampleMs = 1000f / 30f;
    private const float SaveEverySeconds = 20f;
    private const float MaxJumpPerFrame = 90f;
    private const int ForgetAfterFrames = 10;
    private const int MaxTrackedShapes = 300;

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
        public bool IsPlayer;
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
        // The game tick the sample's state is from and the music position on it (-1: unknown).
        public int Tick = -1;
        public float TickMusic;
        public int[] C;
        public int[] Q;
        public float[] K;
        // What the game drew ("v") and its warning bands ("z"), kept as the JSON they're saved
        // as: a boss level draws over a thousand clips a sample, far too many to keep as objects.
        public byte[] V;
        public byte[] Z;
    }

    // A busy sample is capped (boss projectiles, particles) so it can't stall the game.
    private const int MaxNodesPerSample = 3000;
    // Roots this far outside the stage are culled (lasers and walls start just off it).
    private const float CullMargin = 600f;
    private static int _nodesThisSample;
    private static float _nextGc;
    // When reading the display list gets slow (a boss with thousands of clips), the next few
    // samples reuse the last snapshot instead of reading it again.
    private const double SlowSnapshotMs = 20;
    private static int _reuseVisuals;
    private static float _nextStats;
    private static int _statSnapshots, _statNodes, _statMaxNodes, _statFrame;
    private static double _statMs, _statMaxMs;
    private static byte[] _lastVisuals, _lastZones;

    // Warning bands (DangerZoneEffect, drawn by code rather than from art): the container
    // offset to editor coordinates and the rects (x, y, width, height each, from Vector4s).
    private class Zone
    {
        public float[] Offset;
        public bool Flashing;
        public float[] Rects;
    }

    // Symbols seen in this recording: (name, bundle) -> index into the saved table.
    private static readonly List<(string name, string bundle)> Symbols = new();
    private static readonly Dictionary<(string, string), int> SymbolIds = new();
    // On-screen objects are pooled; a pointer keeps its id while it shows the same symbol.
    private static readonly Dictionary<IntPtr, (int sym, int id)> VisualIds = new();
    private static int _nextVisualId = 1;

    private class Recording
    {
        public string Level;
        public string Signature;
        public readonly SortedDictionary<int, Sample> Samples = new();
        // Where each spawn was actually put, keyed by (type, timeMs as in the level file),
        // in creation order; a later pass overwrites by position in that order.
        public readonly Dictionary<(string, string), List<float[]>> Placements = new();
        // idInSong -> (tick, music position) of each spawn's creation in this run.
        public readonly SortedDictionary<int, (int tick, float music)> Created = new();
        // idInSong -> (tick, music position) of each spawn's first shot.
        public readonly SortedDictionary<int, (int tick, float music)> Shots = new();
        // The run's MyMath.roomSeed: with it the editor reproduces every random choice. The
        // placements always belong to this run (a restart re-rolls it and drops them).
        public int? RoomSeed;
        public bool Dirty;
        // A new run doesn't replace the saved one (another run's) until it has as many samples:
        // a short or incidental run (the practice screen's preview, the end screen, a few
        // seconds of play) must not wipe a full recording.
        public int MustReach;
    }

    // Spawns created this frame, read once the game has placed them (next frame).
    private static readonly List<(Spawn spawn, string type, string timeMs, int frame)> NewSpawns = new();
    // How many spawns of each key this pass has placed, to overwrite the right entry.
    private static readonly Dictionary<(string, string), int> PlacedThisPass = new();

    // A spawn's weapon fired (AmmoOwners): keep the tick and clock of its first shot.
    public static void OnShot(int idInSong)
    {
        if (_rec == null || float.IsNaN(_passStart) || NoHit.IsFastForwarding) return;
        try
        {
            var music = NoHit.GetArcadeLevel(quiet: true)?.logicEnemyCreator?.sfxView;
            if (EditorBridge.TryReadMusic(music, out float position, out _))
                lock (Gate) if (!_rec.Shots.ContainsKey(idInSong)) _rec.Shots[idInSong] = (_tick + 1, position);
        }
        catch { /* informational only */ }
    }

    // MetaSpawn.createSpawn postfix (Patches): remember the spawn to read where it lands.
    public static void OnSpawnCreated(MetaSpawn meta, Spawn spawn)
    {
        if (_rec == null || spawn == null || meta == null) return;
        try
        {
            string type = meta.modelSpawn?.id;
            var timeline = meta.metaParams?.metaTimeline ?? meta.getTimeline();
            string timeMs = timeline?.timeInMs?.value;
            if (type != null && timeMs != null)
                NewSpawns.Add((spawn, type, timeMs, Time.frameCount));
            if (!float.IsNaN(_passStart) && !NoHit.IsFastForwarding)
            {
                var music = NoHit.GetArcadeLevel(quiet: true)?.logicEnemyCreator?.sfxView;
                if (EditorBridge.TryReadMusic(music, out float position, out _))
                    // Inside the tick, before OnSceneTick counts it.
                    lock (Gate) _rec.Created[meta.idInSong] = (_tick + 1, position);
            }
        }
        catch { /* informational only */ }
    }

    // Placements are [x, y, rotation] in editor coordinates, rotation from the hazard's view
    // (degrees clockwise; a wall's tells its side).
    private static void ReadPlacements()
    {
        for (int i = NewSpawns.Count - 1; i >= 0; i--)
        {
            var (spawn, type, timeMs, frame) = NewSpawns[i];
            if (frame >= Time.frameCount) continue;
            NewSpawns.RemoveAt(i);
            if (float.IsNaN(_passStart) || NoHit.IsFastForwarding) continue;
            try
            {
                if (spawn.destroyed) continue;
                float rot = spawn.renderComponent?.animView?.rotation ?? 0f;
                var key = (type, timeMs);
                PlacedThisPass.TryGetValue(key, out int n);
                PlacedThisPass[key] = n + 1;
                lock (Gate)
                {
                    if (!_rec.Placements.TryGetValue(key, out var list)) _rec.Placements[key] = list = new List<float[]>();
                    var (sx, sy) = ToScreen(spawn.px, spawn.py);
                    var placed = new[] { sx, sy, rot };
                    if (n < list.Count) list[n] = placed; else list.Add(placed);
                    _rec.Dirty = true;
                }
            }
            catch { /* spawn gone */ }
        }
    }

    // The camera this frame, as the y-down affine from world (stage units centred on the stage,
    // what actors' px/py and the layers' contents use) to the screen (same units). The hazard
    // layers draw with the camera plus shake; CameraFlash.mainCamera has the camera alone
    // (measured on On The Run: layer translation = -camera px, shake only in the layer).
    private const float MaxShake = 60f;
    private static int _cameraFrame = -1;
    private static float[] _camera = { 1, 0, 0, 1, 0, 0 };
    private static bool _cameraMoved, _cameraDisagreed;

    private static float[] Camera()
    {
        if (_cameraFrame == Time.frameCount) return _camera;
        _cameraFrame = Time.frameCount;
        float[] cam = { 1, 0, 0, 1, 0, 0 };
        try
        {
            var layer = CameraFlashContainers.instance?.enemyVisual;
            if (layer != null)
            {
                var lw = DisplayList.WorldAffine(layer);
                if (MathF.Abs(lw[0] * lw[3] - lw[1] * lw[2]) >= 1e-9f)
                {
                    cam = lw;
                    var main = CameraFlash.mainCamera;
                    if (main != null)
                    {
                        // Where the layer would sit without shake.
                        float ex = -(lw[0] * main.px + lw[2] * main.py), ey = -(lw[1] * main.px + lw[3] * main.py);
                        float dx = lw[4] - ex, dy = lw[5] - ey;
                        if (dx * dx + dy * dy <= MaxShake * MaxShake) { cam[4] = ex; cam[5] = ey; }
                        else if (!_cameraDisagreed)
                        {
                            _cameraDisagreed = true;
                            Plugin.Logger.LogInfo($"[recorder] the hazard layer is {dx:0},{dy:0} away from the camera " +
                                $"({main.px:0},{main.py:0} zoom {main.zoomScale:0.00}); following the layer, shake included");
                        }
                    }
                }
            }
        }
        catch { /* keep the identity */ }
        _camera = cam;
        _cameraMoved = MathF.Abs(cam[0] - 1f) > 1e-4f || MathF.Abs(cam[1]) > 1e-4f || MathF.Abs(cam[2]) > 1e-4f ||
                       MathF.Abs(cam[3] - 1f) > 1e-4f || MathF.Abs(cam[4]) > 0.05f || MathF.Abs(cam[5]) > 0.05f;
        return cam;
    }

    // A world point (stage-centred, y down) -> editor screen coordinates.
    private static (float x, float y) ToScreen(float x, float y)
    {
        var k = Camera();
        return (k[0] * x + k[2] * y + k[4] + 640f, k[1] * x + k[3] * y + k[5] + 360f);
    }

    private static float CameraScale()
    {
        var k = Camera();
        return MathF.Sqrt(MathF.Abs(k[0] * k[3] - k[1] * k[2]));
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
        var (sx, sy) = ToScreen(x, y);
        live.Circles.Add(sx);
        live.Circles.Add(sy);
        live.Circles.Add(r * CameraScale());
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
            var (sx, sy) = ToScreen(p.x, p.y);
            live.Quads.Add(sx);
            live.Quads.Add(sy);
        }
    }

    // A hazard may test several shapes per frame; keep all of this frame's, drop older ones.
    private static Live LiveFor(Actor attacker)
    {
        if (_rec == null || attacker == null)
            return null;
        IntPtr ptr = attacker.Pointer;
        if (!LiveShapes.TryGetValue(ptr, out var live))
            // A dash hit-tests the player's own shape (r 75) against things; that's no hazard.
            LiveShapes[ptr] = live = new Live { IsPlayer = attacker.TryCast<Hero>() != null };
        if (live.IsPlayer)
            return null;
        live.Attacker = attacker;
        if (live.Frame != Time.frameCount)
        {
            live.Frame = Time.frameCount;
            live.Circles.Clear();
            live.Quads.Clear();
        }
        return live;
    }

    // The animation a hazard draws with, to tag its collision tracks: its render component's,
    // or the one its spawn names by parameter. Null when unknown.
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
            return anim;
        }
        catch
        {
            // Purely informational; never let it disturb recording.
            return null;
        }
    }

    // Hazards that hit-tested in the last second, for diagnostics.
    public static List<Actor> RecentAttackers() =>
        LiveShapes.Values.Where(l => l.Attacker != null && Time.frameCount - l.Frame < 60).Select(l => l.Attacker).ToList();

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

    // Game ticks (GameScene.update) and the music position the level timeline saw on the last one.
    private static int _tick, _statTick;
    private static float _tickMusic = float.NaN;
    private static bool _tickFailLogged;

    // Called after each game tick (JSB_Init.FixedUpdate, which runs the GameScene.update that
    // moves every hazard).
    public static void OnSceneTick()
    {
        _tick++;
        if (_rec == null) return;
        try
        {
            var music = NoHit.GetArcadeLevel(quiet: true)?.logicEnemyCreator?.sfxView;
            _tickMusic = EditorBridge.TryReadMusic(music, out float position, out _) ? position : float.NaN;
        }
        catch (Exception e)
        {
            _tickMusic = float.NaN;
            if (!_tickFailLogged) Plugin.Logger.LogWarning($"[recorder] reading the music on a tick failed: {e.Message}");
            _tickFailLogged = true;
        }
    }

    // Called from LateUpdate, after the game has run this frame's collision tests.
    public static void Tick()
    {
        // Between the levels of an unattended run the game can still report the previous
        // level while the next one starts: record nothing until it's really playing.
        if (!Plugin.RecordMotion.Value || (AutoRecorder.Active && (!AutoRecorder.RecordingLevel || AutoRecorder.RecordingOff)))
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

        // A restart rolls a new room seed: everything recorded before belongs to the old run.
        int? seed = GameRandom.RoomSeed();
        if (seed != null && seed != _rec.RoomSeed)
            lock (Gate)
            {
                if (_rec.RoomSeed != null)
                {
                    _rec.MustReach = Math.Max(_rec.MustReach, _rec.Samples.Count);
                    _rec.Placements.Clear();
                    _rec.Created.Clear();
                    _rec.Shots.Clear();
                    _rec.Samples.Clear();
                }
                _rec.RoomSeed = seed;
                _rec.Dirty = true;
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
        ReadPlacements();

        int bucket = (int)(position / SampleMs);
        if (bucket != _lastBucket)
        {
            _lastBucket = bucket;
            var sample = BuildSample(position);
            sample.PassStart = _passStart;
            if (!float.IsNaN(_tickMusic))
            {
                sample.Tick = _tick;
                sample.TickMusic = _tickMusic;
            }
            lock (Gate)
            {
                if (!_rec.Samples.TryGetValue(bucket, out var existing) || _passStart <= existing.PassStart + 1f)
                {
                    _rec.Samples[bucket] = sample;
                    _rec.Dirty = true;
                }
            }
        }

        // Reading the display list wraps thousands of game objects a second; each wrapper
        // holds the game object until collected, so collect regularly instead of letting
        // them pile up (a boss level otherwise ran the game out of memory).
        if (Time.unscaledTime >= _nextStats)
        {
            _nextStats = Time.unscaledTime + 5f;
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            Plugin.Logger.LogInfo($"[recorder] at {position / 1000f:0}s: {_statSnapshots} snapshots, " +
                $"avg {(_statSnapshots > 0 ? _statMs / _statSnapshots : 0):0.0}ms max {_statMaxMs:0}ms, " +
                $"clips avg {(_statSnapshots > 0 ? _statNodes / _statSnapshots : 0)} max {_statMaxNodes}, " +
                $"samples {_rec.Samples.Count}, managed {GC.GetTotalMemory(false) / 1048576}MB, " +
                $"process {proc.PrivateMemorySize64 / 1048576}MB, fps {(Time.frameCount - _statFrame) / 5f:0}, " +
                $"ticks/s {(_tick - _statTick) / 5f:0}, tick music {(float.IsNaN(_tickMusic) ? "none" : "ok")}, " +
                $"time offset {EditorBridge.TimelineOffsetMs():0}ms");
            _statTick = _tick;
            _statFrame = Time.frameCount;
            _statSnapshots = 0; _statMs = 0; _statMaxMs = 0; _statNodes = 0; _statMaxNodes = 0;
        }

        if (Time.unscaledTime >= _nextGc)
        {
            _nextGc = Time.unscaledTime + 3f;
            GC.Collect(1);   // the wrappers die young; a full collection of a big heap stalls
        }

        // An unattended run saves each level once, when it ends (AutoRecorder).
        if (Time.unscaledTime >= _nextSave && !AutoRecorder.Active)
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
            PlacedThisPass.Clear();
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
        // A volley of hundreds of shapes in one hazard (a boss's or a dense pattern's bullets,
        // all hit-tested together) isn't followed: matching them frame to frame froze the game
        // even with a grid, since they bunch up. Their shapes are still recorded, as id 0
        // (untracked: the editor draws them without easing between samples); what they look
        // like comes from the recorded visuals anyway.
        if (n > MaxTrackedShapes)
        {
            tracks.Clear();
            return ids;
        }
        var centers = new (float x, float y, float size)[n];
        for (int s = 0; s < n; s++)
            centers[s] = Center(data, s * stride, stride);

        var pairs = new List<(float cost, int s, int t)>();
        void Consider(int s, int t)
        {
            var (x, y, size) = centers[s];
            var tr = tracks[t];
            int df = frame - tr.Frame;
            if (df <= 0 || df > ForgetAfterFrames) return;
            if (Math.Abs(size - tr.Size) > Math.Max(4f, tr.Size * 0.35f)) return;
            float px = tr.X + tr.Vx * df, py = tr.Y + tr.Vy * df;
            float d = MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
            if (d <= MaxJumpPerFrame * df) pairs.Add((d, s, t));
        }
        if ((long)n * tracks.Count <= 40_000)
        {
            for (int s = 0; s < n; s++)
                for (int t = 0; t < tracks.Count; t++)
                    Consider(s, t);
        }
        else
        {
            // A boss's barrage is thousands of shapes a frame, all tested as one hazard:
            // comparing every shape with every track (millions of pairs a frame) froze the game.
            // Bucket recently seen tracks by where they should be now and compare each shape
            // only with the tracks in the cells around it.
            const float Cell = 2f * MaxJumpPerFrame;
            var grid = new Dictionary<long, List<int>>();
            static long CellKey(float x, float y) => ((long)MathF.Floor(x / Cell) << 32) ^ (uint)(int)MathF.Floor(y / Cell);
            for (int t = 0; t < tracks.Count; t++)
            {
                var tr = tracks[t];
                int df = frame - tr.Frame;
                if (df <= 0 || df > 2) continue;   // only tracks seen in the last two frames
                long key = CellKey(tr.X + tr.Vx * df, tr.Y + tr.Vy * df);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                list.Add(t);
            }
            for (int s = 0; s < n; s++)
            {
                var (x, y, _) = centers[s];
                long cx = (long)MathF.Floor(x / Cell), cy = (long)MathF.Floor(y / Cell);
                for (long ix = cx - 1; ix <= cx + 1; ix++)
                    for (long iy = cy - 1; iy <= cy + 1; iy++)
                        if (grid.TryGetValue((ix << 32) ^ (uint)(int)iy, out var list))
                            foreach (int t in list) Consider(s, t);
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
        byte[] visuals, zones;
        if (_reuseVisuals > 0 && _lastVisuals != null)
        {
            // No snapshot of its own: the editor eases between the samples around it (repeating
            // the last snapshot froze the art and then made it jump).
            _reuseVisuals--;
            visuals = null;
            zones = null;
        }
        else
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            visuals = SnapshotVisuals(out zones);
            double ms = clock.Elapsed.TotalMilliseconds;
            _statSnapshots++; _statMs += ms; _statMaxMs = Math.Max(_statMaxMs, ms);
            _statNodes += _nodesThisSample; _statMaxNodes = Math.Max(_statMaxNodes, _nodesThisSample);
            _reuseVisuals = ms > SlowSnapshotMs ? Math.Min(4, (int)(ms / SlowSnapshotMs)) : 0;
            _lastVisuals = visuals;
            _lastZones = zones;
        }
        return new Sample { T = t, C = circles.ToArray(), Q = quads.ToArray(), V = visuals, Z = zones, K = CameraInEditor() };
    }

    // The camera as editor coordinates world -> screen (null at rest): screen = k * world.
    private static float[] CameraInEditor()
    {
        var k = Camera();
        if (!_cameraMoved) return null;
        return new[]
        {
            k[0], k[1], k[2], k[3],
            k[4] + 640f - (k[0] * 640f + k[2] * 360f),
            k[5] + 360f - (k[1] * 640f + k[3] * 360f),
        };
    }

    // Every visible hazard clip on the hazard layers (not the players, not UI containers) as
    // the JSON array saved as "v", and the warning bands found in those containers ("z").
    // Clips are written as they're walked; each one's matrix comes from its world matrix and
    // its parent's (passed down, so no clip is looked up twice).
    private static byte[] SnapshotVisuals(out byte[] zonesJson)
    {
        zonesJson = null;
        var zones = new List<Zone>();
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(4096);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            _nodesThisSample = 0;
            try
            {
                var seen = new HashSet<IntPtr>();
                MapSpawnClips();
                var camera = Camera();
                // The background layer first (drawn behind): a level's own scenery there is
                // part of the level (Spectra's hex stage is one clip on it, with collisions).
                var layers = new List<(string, DisplayObjectContainer)> { ("background", CameraFlashContainers.instance?.backgroundVisual) };
                layers.AddRange(DisplayList.Layers());
                foreach (var (name, layer) in layers)
                {
                    if (name == "hero" || layer == null) continue;
                    var layerWorld = DisplayList.WorldAffine(layer);
                    if (MathF.Abs(layerWorld[0] * layerWorld[3] - layerWorld[1] * layerWorld[2]) < 1e-9f) continue;
                    // Roots relative to their layer, then through the camera: on screen, less shake.
                    var toScreen = DisplayList.Multiply(camera, DisplayList.Invert(layerWorld));
                    bool scenery = name == "background";
                    foreach (var d in DisplayList.Children(layer))
                    {
                        if (_nodesThisSample >= MaxNodesPerSample) break;
                        if (d.className == "EMC") { CollectZones(d, 0f, 0f, zones); continue; }
                        if (scenery)
                        {
                            // The usual backdrop (FX_BG_*) isn't level content.
                            string cls = d.className;
                            if (string.IsNullOrEmpty(cls) || cls.StartsWith("FX_BG")) continue;
                            if (SceneryClasses.Add(cls)) Plugin.Logger.LogInfo($"[recorder] recording scenery {cls}");
                        }
                        WriteVisual(w, d, toScreen, root: true, seen, scenery ? toScreen : null);
                    }
                }
                foreach (var gone in VisualIds.Keys.Where(k => !seen.Contains(k)).ToList())
                    VisualIds.Remove(gone);
            }
            catch (Exception e)
            {
                if (!_visualsFailed)
                {
                    _visualsFailed = true;
                    Plugin.Logger.LogWarning($"[recorder] couldn't read the display list: {e.Message}");
                }
            }
            w.WriteEndArray();
        }
        if (zones.Count > 0) zonesJson = ZonesJson(zones);
        return buffer.WrittenSpan.ToArray();
    }
    private static bool _visualsFailed;

    private static void CollectZones(DisplayObject d, float ox, float oy, List<Zone> zones)
    {
        if (!d.visible) return;
        ox += d.x;
        oy += d.y;
        var dz = d.TryCast<DangerZoneEffect>();
        if (dz != null)
        {
            int n = Math.Min(dz.numRects, DangerZoneEffect.MAX_RECTS);
            var rects = dz.rects;
            if (n <= 0 || rects == null) return;
            // Bands are axis-aligned: the camera moves and scales them (it never turns here).
            float s = CameraScale();
            var flat = new float[n * 4];
            for (int i = 0; i < n && i < rects.Length; i++)
            {
                var r = rects[i];
                flat[i * 4] = r.x * s; flat[i * 4 + 1] = r.y * s; flat[i * 4 + 2] = r.z * s; flat[i * 4 + 3] = r.w * s;
            }
            var (sx, sy) = ToScreen(ox, oy);
            zones.Add(new Zone { Offset = new[] { sx, sy }, Flashing = dz.flashingWarning, Rects = flat });
            return;
        }
        var container = d.TryCast<DisplayObjectContainer>();
        if (container != null)
            foreach (var child in DisplayList.Children(container))
                CollectZones(child, ox, oy, zones);
    }

    // One clip and the clips in it, in the format at the top. A clip's own tint is as the
    // game's shader takes it: rgb with 0.5 = unchanged, below multiplies (2c), above adds
    // towards white (2c - 1); written only when not neutral.
    // scenery: a level-sized clip (Spectra's stage) whose origin can be far off screen while
    // its pieces are on it, so it isn't culled by where its origin is.
    private static bool WriteVisual(Utf8JsonWriter w, DisplayObject d, float[] invParent, bool root, HashSet<IntPtr> seen, float[] scenery = null)
    {
        bool visible = d.visible || (scenery != null && SceneryShownInUpdate.Contains(d.Pointer));
        if (!visible || d.alpha <= 0.004f) return false;
        var mc = d.TryCast<MovieClip>();
        if (mc == null) return false;   // sprites and text come from the exported art
        string cls = d.className;
        if (string.IsNullOrEmpty(cls) || cls.StartsWith("COL_")) return false;
        if (root && (cls == "EMC" || cls.StartsWith("HERO"))) return false;

        // The clip's x/rotation/scale properties leave out transforms the art itself carries
        // (a beam's warning line is turned 90 degrees inside its parent and reports 0), so
        // take the matrix the game draws with: local = parent world^-1 * world. A root is
        // taken relative to its layer and put through the camera (without its shake).
        var tr = d.transform;
        var wm = tr.concatenedMatrix;
        var world = new[] { wm.m00, -wm.m10, -wm.m01, wm.m11, wm.m03, -wm.m13 };
        var local = DisplayList.Multiply(invParent, world);
        if (root)
        {
            local[4] += 640f; local[5] += 360f;   // layers sit at the stage centre
            if (scenery == null && (local[4] < -CullMargin || local[4] > 1280f + CullMargin || local[5] < -CullMargin || local[5] > 720f + CullMargin))
                return false;
        }
        int sym = SymbolFor(cls, d);
        int id = 0;
        if (root)
        {
            var key = d.Pointer;
            seen.Add(key);
            if (!VisualIds.TryGetValue(key, out var known) || known.sym != sym)
                VisualIds[key] = known = (sym, _nextVisualId++);
            id = known.id;
        }

        w.WriteStartArray();
        if (root) w.WriteNumberValue(id);
        w.WriteNumberValue(sym);
        w.WriteNumberValue(mc.currentFrame);
        w.WriteNumberValue(MathF.Round(d.alpha, 2));
        bool identity = MathF.Abs(local[0] - 1f) < 1e-4f && MathF.Abs(local[1]) < 1e-4f && MathF.Abs(local[2]) < 1e-4f &&
                        MathF.Abs(local[3] - 1f) < 1e-4f && MathF.Abs(local[4]) < 0.05f && MathF.Abs(local[5]) < 0.05f;
        if (identity && !root) w.WriteNumberValue(0);
        else
        {
            w.WriteStartArray();
            for (int i = 0; i < 4; i++) w.WriteNumberValue(MathF.Round(local[i], 4));
            w.WriteNumberValue(MathF.Round(local[4], 1));
            w.WriteNumberValue(MathF.Round(local[5], 1));
            w.WriteEndArray();
        }
        _nodesThisSample++;

        float det = world[0] * world[3] - world[1] * world[2];
        if (MathF.Abs(det) >= 1e-9f)   // a clip scaled to nothing shows nothing inside it
        {
            var invWorld = DisplayList.Invert(world);
            foreach (var child in DisplayList.Children(mc))
            {
                if (_nodesThisSample >= MaxNodesPerSample) break;
                WriteVisual(w, child, invWorld, root: false, seen);
            }
        }

        Color? tint = null;
        try
        {
            Color c = tr.colorTransform.color;
            if (MathF.Abs(c.r - 0.5f) >= 0.002f || MathF.Abs(c.g - 0.5f) >= 0.002f || MathF.Abs(c.b - 0.5f) >= 0.002f) tint = c;
        }
        catch { /* no colour transform */ }
        (int id, string type) spawn = default;
        bool hasSpawn = root && SpawnOfClip.TryGetValue(d.Pointer, out spawn);
        if (tint != null || hasSpawn)
        {
            // After the kids, as an object, so readers that only take arrays as kids skip it.
            w.WriteStartObject();
            if (tint is Color c)
            {
                w.WriteStartArray("c");
                w.WriteNumberValue(MathF.Round(c.r, 3));
                w.WriteNumberValue(MathF.Round(c.g, 3));
                w.WriteNumberValue(MathF.Round(c.b, 3));
                w.WriteEndArray();
            }
            if (hasSpawn)
            {
                w.WriteStartArray("s");
                w.WriteNumberValue(spawn.id);
                w.WriteStringValue(spawn.type);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteEndArray();
        return true;
    }

    // The spawn behind each hazard's clip this sample: its root clip (the view's anim, or the
    // container it sits in) -> (MetaSpawn.idInSong, spawn type), from the live enemies.
    private static readonly Dictionary<IntPtr, (int id, string type)> SpawnOfClip = new();
    private static void MapSpawnClips()
    {
        SpawnOfClip.Clear();
        try
        {
            var actors = GameScene.instance?.enemyManager?.actorList;
            if (actors == null) return;
            for (int i = 0; i < actors.Count; i++)
            {
                var spawn = actors[i]?.TryCast<Spawn>();
                var meta = spawn?.metaSpawn;
                var view = spawn?.renderComponent?.animView;
                if (meta == null || view == null) continue;
                var info = (meta.idInSong, meta.modelSpawn?.id ?? "");
                if (view.anim != null) SpawnOfClip[view.anim.Pointer] = info;
                if (view.visual != null) SpawnOfClip[view.visual.Pointer] = info;
            }
            // Ammo carries the spawn whose weapon shot it (AmmoOwners).
            var ammo = GameScene.instance?.ammoManager?.actorList;
            var live = new HashSet<IntPtr>();
            for (int i = 0; ammo != null && i < ammo.Count; i++)
            {
                var a = ammo[i];
                if (a == null) continue;
                live.Add(a.Pointer);
                var view = a.renderComponent?.animView;
                if (view == null || !AmmoOwners.TryGet(a.Pointer, out var info)) continue;
                if (view.anim != null) SpawnOfClip[view.anim.Pointer] = info;
                if (view.visual != null) SpawnOfClip[view.visual.Pointer] = info;
            }
            AmmoOwners.Prune(live);
            // Children of FxSquareLine / FxSquareLine2 / FxSquareLineRounded / FxDNALine carry the
            // spawn that made them (ChildOwners).
            ChildOwners.ForEachLive((a, info) =>
            {
                var view = a.renderComponent?.animView;
                if (view == null) return;
                if (view.anim != null) SpawnOfClip[view.anim.Pointer] = info;
                if (view.visual != null) SpawnOfClip[view.visual.Pointer] = info;
            });
        }
        catch (Exception e)
        {
            if (!_spawnMapFailed) { _spawnMapFailed = true; Plugin.Logger.LogWarning($"[recorder] couldn't map clips to spawns: {e.Message}"); }
        }
    }
    private static bool _spawnMapFailed;

    // The symbol table index for a clip's class: its bundle is looked up once per class.
    private static readonly Dictionary<string, int> SymbolByClass = new();
    // Background-layer classes recorded as level scenery, logged once each.
    private static readonly HashSet<string> SceneryClasses = new();

    // Spectra's stage reads as hidden in LateUpdate for much of the level (25-67 s on normal)
    // while it's visible in Update, where the bridge's /display sees it, and on screen. The
    // game seems to toggle it within the frame (culling by its far-off origin), so scenery
    // counts as shown when it was visible during Update (PracticeRunner.Update calls this).
    private static readonly HashSet<IntPtr> SceneryShownInUpdate = new();
    public static void NoteScenery()
    {
        SceneryShownInUpdate.Clear();
        if (_rec == null) return;
        try
        {
            foreach (var d in DisplayList.Children(CameraFlashContainers.instance?.backgroundVisual))
                if (d.visible) SceneryShownInUpdate.Add(d.Pointer);
        }
        catch { /* layer gone */ }
    }
    private static int SymbolFor(string cls, DisplayObject d)
    {
        if (SymbolByClass.TryGetValue(cls, out int id)) return id;
        id = SymbolId(cls, DisplayList.BundleOf(d));
        SymbolByClass[cls] = id;
        return id;
    }

    private static byte[] ZonesJson(List<Zone> zones)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var z in zones)
            {
                w.WriteStartArray();
                w.WriteNumberValue(MathF.Round(z.Offset[0], 1));
                w.WriteNumberValue(MathF.Round(z.Offset[1], 1));
                w.WriteNumberValue(z.Flashing ? 1 : 0);
                foreach (float v in z.Rects) w.WriteNumberValue(MathF.Round(v, 1));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static int SymbolId(string name, string bundle)
    {
        var key = (name, bundle ?? "");
        if (!SymbolIds.TryGetValue(key, out int id))
        {
            SymbolIds[key] = id = Symbols.Count;
            lock (Gate) Symbols.Add((name, bundle ?? ""));
            _rec.Dirty = true;
        }
        return id;
    }

    private static void Begin(string level, string signature)
    {
        _rec = new Recording { Level = level, Signature = signature, RoomSeed = GameRandom.RoomSeed() };
        _lastBucket = int.MinValue;
        _nextSave = Time.unscaledTime + SaveEverySeconds;
        _passStart = float.NaN;
        _lastPos = float.NegativeInfinity;
        _lastTickTime = Time.unscaledTime;
        LiveShapes.Clear();
        _lastVisuals = _lastZones = null;
        _reuseVisuals = 0;
        _cameraDisagreed = false;
        NewSpawns.Clear();
        PlacedThisPass.Clear();
        lock (Gate) { TrackVisuals.Clear(); Symbols.Clear(); }
        SymbolIds.Clear();
        SymbolByClass.Clear();
        VisualIds.Clear();

        // Continue an earlier recording of the same level content (not when re-recording).
        if (AutoRecorder.Active && AutoRecorder.Redo)
        {
            Plugin.Logger.LogInfo($"Re-recording '{level}' from scratch");
            return;
        }
        string path = PathFor(level, signature);
        try
        {
            // Before vanilla and custom recordings were kept apart, both used <LEVEL>.json.
            string shared = Path.Combine(Dir, LevelDump.FileName(level));
            if (!File.Exists(path) && path != shared && File.Exists(shared)) path = shared;
            if (!File.Exists(path)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("signature").GetString() != signature) return;
            // Format 9 is format 10 for any level whose camera stays put (the rest need re-recording).
            if (!root.TryGetProperty("format", out var format) || format.GetInt32() < 9 || format.GetInt32() > FormatVersion) return;
            // A recording holds one run: another run (other room seed, or saved before seeds
            // were) rolled other random placements, and mixing the two makes hazards jump
            // between layouts. Start fresh instead.
            bool sameRun = root.TryGetProperty("roomSeed", out var savedSeed) && savedSeed.ValueKind == JsonValueKind.Number &&
                           savedSeed.GetInt32() == _rec.RoomSeed;
            if (!sameRun)
            {
                // Recorded from scratch, and saved over the old run only once it has as many
                // samples (MustReach); the old one is also kept in replaced/ (latest per level).
                _rec.MustReach = root.TryGetProperty("samples", out var oldSamples) ? oldSamples.GetArrayLength() : 0;
                string replaced = Path.Combine(Dir, "replaced");
                Directory.CreateDirectory(replaced);
                File.Copy(path, Path.Combine(replaced, Path.GetFileName(path)), overwrite: true);
                Plugin.Logger.LogInfo($"The motion recording for '{level}' is from another run; recording from scratch, saved once it has {_rec.MustReach} samples (old one kept in replaced/)");
                return;
            }
            if (root.TryGetProperty("placements", out var placements))
                foreach (var p in placements.EnumerateArray())
                {
                    var key = (p[0].GetString(), p[1].GetString());
                    if (!_rec.Placements.TryGetValue(key, out var list)) _rec.Placements[key] = list = new List<float[]>();
                    list.Add(new[] { p[2].GetSingle(), p[3].GetSingle(), p[4].GetSingle() });
                }
            if (root.TryGetProperty("symbols", out var symbols))
                foreach (var s in symbols.EnumerateArray())
                {
                    string name = s[0].GetString(), bundle = s[1].GetString();
                    SymbolIds[(name, bundle)] = Symbols.Count;
                    lock (Gate) Symbols.Add((name, bundle));
                }
            int maxVisualId = 0;
            foreach (var s in root.GetProperty("samples").EnumerateArray())
            {
                float t = s.GetProperty("t").GetSingle();
                byte[] visuals = null, zones = null;
                if (s.TryGetProperty("v", out var v))
                {
                    visuals = System.Text.Encoding.UTF8.GetBytes(v.GetRawText());
                    foreach (var n in v.EnumerateArray()) maxVisualId = Math.Max(maxVisualId, n[0].GetInt32());
                }
                if (s.TryGetProperty("z", out var z)) zones = System.Text.Encoding.UTF8.GetBytes(z.GetRawText());
                float[] camera = s.TryGetProperty("k", out var k) ? k.EnumerateArray().Select(e => e.GetSingle()).ToArray() : null;
                _rec.Samples[(int)(t / SampleMs)] = new Sample
                {
                    T = t,
                    PassStart = s.TryGetProperty("p", out var p) ? p.GetSingle() : 0f,
                    Tick = s.TryGetProperty("f", out var f) ? f.GetInt32() : -1,
                    TickMusic = s.TryGetProperty("m", out var m) ? m.GetSingle() : 0f,
                    C = s.GetProperty("c").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                    Q = s.GetProperty("q").EnumerateArray().Select(e => e.GetInt32()).ToArray(),
                    V = visuals,
                    Z = zones,
                    K = camera,
                };
            }
            _nextVisualId = 1 + maxVisualId;
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

        List<Sample> samples;
        Dictionary<int, string> trackVisuals;
        List<(string name, string bundle)> symbols;
        List<((string, string) Key, List<float[]>)> placements;
        List<KeyValuePair<int, (int tick, float music)>> created, shots;
        int? roomSeed;
        lock (Gate)
        {
            if (!rec.Dirty) return;
            // Stays dirty, saved once it gets there (within 10%: slow frames leave gaps).
            if (rec.Samples.Count < rec.MustReach * 0.9f) return;
            rec.Dirty = false;
            roomSeed = rec.RoomSeed;
            samples = rec.Samples.Values.ToList();
            symbols = Symbols.ToList();
            placements = rec.Placements.Select(kv => (kv.Key, kv.Value.ToList())).ToList();
            created = rec.Created.ToList();
            shots = rec.Shots.ToList();
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
                string path = PathFor(rec.Level, rec.Signature), tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                using (var stream = File.Create(tmp))
                using (var w = new Utf8JsonWriter(stream))
                {
                    w.WriteStartObject();
                    w.WriteNumber("format", FormatVersion);
                    w.WriteString("level", rec.Level);
                    w.WriteString("signature", rec.Signature);
                    w.WriteNumber("sampleMs", SampleMs);
                    if (roomSeed is int seed) w.WriteNumber("roomSeed", seed);
                    // track id -> the animation that hazard draws with
                    w.WriteStartObject("tracks");
                    foreach (var (id, visual) in trackVisuals)
                        w.WriteString(id.ToString(), visual);
                    w.WriteEndObject();
                    // visual symbol index -> [name, asset bundle]
                    w.WriteStartArray("symbols");
                    foreach (var (name, bundle) in symbols)
                    {
                        w.WriteStartArray();
                        w.WriteStringValue(name);
                        w.WriteStringValue(bundle);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                    // [type, timeMs, x, y, rotation] per spawn, in creation order per (type, timeMs)
                    w.WriteStartArray("placements");
                    foreach (var (key, list) in placements)
                        foreach (var p in list)
                        {
                            w.WriteStartArray();
                            w.WriteStringValue(key.Item1);
                            w.WriteStringValue(key.Item2);
                            w.WriteNumberValue(MathF.Round(p[0], 1));
                            w.WriteNumberValue(MathF.Round(p[1], 1));
                            w.WriteNumberValue(MathF.Round(p[2], 1));
                            w.WriteEndArray();
                        }
                    w.WriteEndArray();
                    foreach (var (name, table) in new[] { ("created", created), ("shots", shots) })
                    {
                        w.WriteStartArray(name);
                        foreach (var (id, (tick, music)) in table)
                        {
                            w.WriteStartArray();
                            w.WriteNumberValue(id);
                            w.WriteNumberValue(tick);
                            w.WriteNumberValue(MathF.Round(music, 1));
                            w.WriteEndArray();
                        }
                        w.WriteEndArray();
                    }
                    w.WriteStartArray("samples");
                    foreach (var s in samples)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("t", MathF.Round(s.T, 1));
                        w.WriteNumber("p", MathF.Round(s.PassStart));
                        if (s.Tick >= 0)
                        {
                            w.WriteNumber("f", s.Tick);
                            w.WriteNumber("m", MathF.Round(s.TickMusic, 1));
                        }
                        WriteInts(w, "c", s.C);
                        WriteInts(w, "q", s.Q);
                        if (s.K != null)
                        {
                            w.WriteStartArray("k");
                            for (int i = 0; i < 4; i++) w.WriteNumberValue(MathF.Round(s.K[i], 4));
                            w.WriteNumberValue(MathF.Round(s.K[4], 1));
                            w.WriteNumberValue(MathF.Round(s.K[5], 1));
                            w.WriteEndArray();
                        }
                        if (s.V != null)
                        {
                            w.WritePropertyName("v");
                            w.WriteRawValue(s.V, skipInputValidation: true);
                        }
                        if (s.Z != null)
                        {
                            w.WritePropertyName("z");
                            w.WriteRawValue(s.Z, skipInputValidation: true);
                        }
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

    // Vanilla and custom content are recorded apart, so opening either shows its own motion:
    // <LEVEL>.json for the vanilla level, <LEVEL>.custom.json for the custom file.
    private static string PathFor(string level, string signature)
    {
        string file = LevelDump.FileName(level);
        return Path.Combine(Dir, signature == "vanilla" ? file : file.Replace(".json", ".custom.json"));
    }
}
