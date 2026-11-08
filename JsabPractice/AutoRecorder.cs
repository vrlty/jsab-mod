using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace JsabPractice;

// Records levels unattended: starts each song in practice mode (normal and/or hardcore),
// keeps the player invincible, plays it sped up and lets the Recorder capture it, then moves
// on. Driven from the editor bridge:
//   POST /autorecord {"only": ["FOX"], "difficulty": "both"|"normal"|"hard", "speed": 2,
//                     "redo": false}   -> queue the songs and start
//   POST /autorecord/stop              -> stop after restoring speed
// Progress shows in /status (autoRecord).
internal static class AutoRecorder
{
    private enum State { Idle, Starting, Playing }

    private sealed class Job
    {
        public ModelStoryCheckpoint Song;
        public string Id;
        public ModelGameDifficulty Difficulty;
        public bool Hard;
    }

    private static readonly Queue<Job> Jobs = new();
    private static Job _job;
    private static State _state = State.Idle;
    private static float _since;
    private static float _speed = 2f;
    private static bool _redo;
    private static int _done, _skipped, _failed, _total;
    private static string _last = "";
    private static float _lastPos;
    private static float _stalledSince;
    private static DynamicMusic _music;

    public static bool Active => _state != State.Idle || Jobs.Count > 0;

    public static object Progress => !Active && _total == 0 ? null : new
    {
        active = Active,
        current = _job == null ? null : $"{_job.Id}{(_job.Hard ? " (hardcore)" : "")}",
        state = _state.ToString(),
        done = _done,
        skipped = _skipped,
        failed = _failed,
        total = _total,
        last = _last,
    };

    public static string Start(string body)
    {
        var only = new List<string>();
        string difficulty = "both";
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("only", out var o) && o.ValueKind == JsonValueKind.Array)
                only.AddRange(o.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)));
            if (root.TryGetProperty("difficulty", out var d)) difficulty = d.GetString() ?? "both";
            _speed = root.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? Mathf.Clamp(sp.GetSingle(), 1f, 4f) : 2f;
            _redo = root.TryGetProperty("redo", out var r) && r.ValueKind == JsonValueKind.True;
        }

        Jobs.Clear();
        var all = ModelStoryCheckpointEnum.ALL;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var song = all[i];
            string id = song?.id;
            if (string.IsNullOrEmpty(id)) continue;
            string name = SafeName(song);
            if (only.Count > 0 && !only.Any(s => id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            // Map nodes without a song can't be played; anything else is tried (a start that
            // doesn't happen within the timeout is skipped).
            bool hasTrack;
            try { hasTrack = song.modelSoundTrack != null; }
            catch { hasTrack = false; }
            if (!hasTrack) continue;
            if (difficulty != "hard") Jobs.Enqueue(new Job { Song = song, Id = id, Difficulty = ModelGameDifficultyEnum.NORMAL, Hard = false });
            if (difficulty != "normal") Jobs.Enqueue(new Job { Song = song, Id = id, Difficulty = ModelGameDifficultyEnum.HARD, Hard = true });
        }
        _total = Jobs.Count;
        _done = _skipped = _failed = 0;
        _last = "";
        Plugin.Logger.LogInfo($"[autorecord] queued {_total} runs at {_speed}x");
        _job = null;
        _state = State.Idle;
        NextJob();
        return JsonSerializer.Serialize(new { ok = true, queued = _total, speed = _speed });
    }

    private static string SafeName(ModelStoryCheckpoint song)
    {
        try { return song.name ?? ""; } catch { return ""; }
    }

    // Every song the game knows, for picking what to record: GET /songs.
    public static string Songs()
    {
        var list = new List<object>();
        var all = ModelStoryCheckpointEnum.ALL;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var song = all[i];
            if (song == null) continue;
            bool missing = true, track = false, loaded = false;
            string level = null, soundtrack = null;
            try { missing = song.isMissingSong(); } catch { }
            try { track = song.modelSoundTrack != null; soundtrack = song.modelSoundTrack?.id; } catch { }
            try { loaded = song.isMetasongLoaded(); level = loaded ? song.metaSong?.name : null; } catch { }
            list.Add(new { id = song.id, name = SafeName(song), missing, track, soundtrack, loaded, level });
        }
        return JsonSerializer.Serialize(new { ok = true, songs = list });
    }

    public static string Stop()
    {
        Jobs.Clear();
        Finish("stopped");
        _state = State.Idle;
        _job = null;
        return JsonSerializer.Serialize(new { ok = true });
    }

    // Called every frame (PracticeRunner.Update).
    public static void Update()
    {
        if (_state == State.Idle) return;
        var arcade = NoHit.GetArcadeLevel(quiet: true);
        var song = arcade?.currentSong;
        float now = Time.unscaledTime;

        if (_state == State.Starting)
        {
            if (song != null && EditorBridge.TryReadMusic(arcade.logicEnemyCreator?.sfxView, out float p, out bool playing) && playing && p > 0f)
            {
                string level = LevelDump.LevelName(song);
                if (_job.Hard && !level.StartsWith("JSBHRD_", StringComparison.Ordinal))
                {
                    Plugin.Logger.LogInfo($"[autorecord] {_job.Id} has no hardcore level ({level}); skipping");
                    _skipped++; _last = $"{_job.Id}: no hardcore";
                    NextJob();
                    return;
                }
                if (!_redo && AlreadyRecorded(level, arcade))
                {
                    Plugin.Logger.LogInfo($"[autorecord] {level} is already recorded; skipping");
                    _skipped++; _last = $"{level}: already recorded";
                    NextJob();
                    return;
                }
                _music = arcade.logicEnemyCreator.sfxView;
                _state = State.Playing;
                _since = now;
                _lastPos = p;
                _stalledSince = now;
                Plugin.Logger.LogInfo($"[autorecord] recording {level}");
                return;
            }
            if (now - _since > 45f)
            {
                Plugin.Logger.LogWarning($"[autorecord] {_job.Id} didn't start; skipping");
                _failed++; _last = $"{_job.Id}: didn't start";
                NextJob();
            }
            return;
        }

        // Playing: keep the speed (the game resets it on some transitions) and watch for the end.
        if (song == null)
        {
            Plugin.Logger.LogInfo($"[autorecord] {_job.Id} left the level");
            Complete();
            return;
        }
        var music = arcade.logicEnemyCreator?.sfxView;
        if (Time.timeScale != _speed) Time.timeScale = _speed;
        try { if (music != null && music.playbackSpeed != _speed) music.playbackSpeed = _speed; }
        catch { /* no clip */ }

        bool ok = EditorBridge.TryReadMusic(music, out float pos, out bool isPlaying);
        float lengthMs = 0f;
        try { if (music != null) lengthMs = music.length * 1000f; }
        catch { /* no clip */ }
        if (ok && pos > _lastPos + 1f) { _lastPos = pos; _stalledSince = now; }
        bool atEnd = lengthMs > 0f && _lastPos >= lengthMs - 400f;
        bool stalled = now - _stalledSince > 6f;
        if (atEnd || stalled)
        {
            Plugin.Logger.LogInfo($"[autorecord] {_job.Id} {(atEnd ? "finished" : "stopped moving")} at {_lastPos / 1000f:0.0}s");
            Complete();
        }
    }

    private static void Complete()
    {
        Recorder.Save(background: false);
        _done++;
        _last = $"{_job?.Id} recorded to {_lastPos / 1000f:0.0}s";
        NextJob();
    }

    private static void NextJob()
    {
        RestoreSpeed();
        if (Jobs.Count == 0)
        {
            Finish("done");
            return;
        }
        _job = Jobs.Dequeue();
        _state = State.Starting;
        _since = Time.unscaledTime;
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<ModelStoryCheckpoint>();
            list.Add(_job.Song);
            ArcadeLevelScene.StartPractice(list, _job.Difficulty);
            Plugin.Logger.LogInfo($"[autorecord] starting {_job.Id} ({(_job.Hard ? "hardcore" : "normal")})");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[autorecord] couldn't start {_job.Id}: {e.Message}");
            _failed++; _last = $"{_job.Id}: {e.Message}";
            _state = State.Idle;
            NextJob();
        }
    }

    private static void Finish(string why)
    {
        RestoreSpeed();
        if (_state != State.Idle || _job != null)
            Plugin.Logger.LogInfo($"[autorecord] {why}: {_done} recorded, {_skipped} skipped, {_failed} failed");
        _state = State.Idle;
        _job = null;
    }

    private static void RestoreSpeed()
    {
        Time.timeScale = 1f;
        try { if (_music != null) _music.playbackSpeed = 1f; }
        catch { /* gone */ }
        _music = null;
    }

    // A recording of this level that already reaches (nearly) the end of the song.
    private static bool AlreadyRecorded(string level, ActorArcadeLevelLogic arcade)
    {
        try
        {
            string path = Path.Combine(Recorder.Dir, LevelDump.FileName(level));
            if (!File.Exists(path)) return false;
            float lengthMs = arcade.logicEnemyCreator.sfxView.length * 1000f;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.GetProperty("signature").GetString() != "vanilla") return false;
            if (!root.TryGetProperty("format", out var f) || f.GetInt32() < 9) return false;
            var samples = root.GetProperty("samples");
            int n = samples.GetArrayLength();
            return n > 0 && samples[n - 1].GetProperty("t").GetSingle() >= lengthMs * 0.95f;
        }
        catch
        {
            return false;
        }
    }
}
