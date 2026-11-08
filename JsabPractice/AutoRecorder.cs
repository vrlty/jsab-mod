using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HarmonyLib;
using UnityEngine;

namespace JsabPractice;

// Records levels unattended: starts each song in practice mode (normal and/or hardcore),
// keeps the player invincible, plays it sped up and lets the Recorder capture it, then moves
// on. Driven from the editor bridge:
//   POST /autorecord {"names": ["FOX", ...] | "only": ["FOX"], "difficulty": "both"|"normal"|
//                     "hard", "speed": 2, "redo": false}   -> queue the songs and start
// names match song names exactly (trimmed, any case; the editor sends its levels' songName),
// only matches any part of a song's id or name.
//   POST /autorecord/stop              -> stop after restoring speed
// Progress shows in /status (autoRecord).
//
// Levels start with ArcadeLevelScene.StartPractice, but never from the main menu: from there
// the level's start (LogicArcadeStartLevel.initLevel) hits a missing object and the game's
// asset loader then throws every frame (the menu also stays drawn), which tanks the frame
// rate. So from the menus the practice screen is opened first (UI_Practice.loadAndCreate),
// and the level starts once it's up; after a level ends the next one starts right away,
// which works cleanly.
internal static class AutoRecorder
{
    private enum State { Idle, Opening, Starting, Playing }

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
    // record: false plays the queue without recording (to tell the game's own load from the
    // recorder's).
    public static bool RecordingOff;
    private static int _done, _skipped, _failed, _total;
    private static string _last = "";
    private static float _lastPos;
    private static float _stalledSince;
    private static int _stalledFrames;
    private static DynamicMusic _music;
    private static bool _openedPractice;

    // Set when the practice screen comes up (AutoRecorderPatches).
    public static UI_Practice PracticeUI;

    public static bool Active => _state != State.Idle || Jobs.Count > 0;
    // The recorder records only while a queued level is actually playing.
    public static bool RecordingLevel => _state == State.Playing;

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
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string difficulty = "both";
        using (var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body))
        {
            var root = doc.RootElement;
            if (root.TryGetProperty("only", out var o) && o.ValueKind == JsonValueKind.Array)
                only.AddRange(o.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)));
            if (root.TryGetProperty("names", out var nm) && nm.ValueKind == JsonValueKind.Array)
                foreach (var e in nm.EnumerateArray())
                    if (!string.IsNullOrWhiteSpace(e.GetString())) names.Add(e.GetString().Trim());
            if (root.TryGetProperty("difficulty", out var d)) difficulty = d.GetString() ?? "both";
            _speed = root.TryGetProperty("speed", out var sp) && sp.ValueKind == JsonValueKind.Number ? Mathf.Clamp(sp.GetSingle(), 1f, 4f) : 2f;
            _redo = root.TryGetProperty("redo", out var r) && r.ValueKind == JsonValueKind.True;
            RecordingOff = root.TryGetProperty("record", out var rec) && rec.ValueKind == JsonValueKind.False;
        }

        Jobs.Clear();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = ModelStoryCheckpointEnum.ALL;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var song = all[i];
            string id = song?.id;
            if (string.IsNullOrEmpty(id)) continue;
            string name = SafeName(song).Trim();
            if (names.Count > 0 && !names.Contains(name)) continue;
            if (only.Count > 0 && !only.Any(s => id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                 name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            // The same song can be listed more than once (story and extra): record it once.
            if (!matched.Add(name)) continue;
            // Map nodes without a song can't be played; anything else is tried (a start that
            // doesn't happen within the timeout is skipped).
            bool hasTrack;
            try { hasTrack = song.modelSoundTrack != null; }
            catch { hasTrack = false; }
            if (!hasTrack) continue;
            // Story ambience, menu and challenge music aren't levels: only songs on an album
            // (the ones practice mode lists), unless picked explicitly with "only".
            if (only.Count == 0 && !IsPracticeSong(song)) continue;
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
        var unmatched = names.Where(n => !matched.Contains(n)).ToList();
        if (unmatched.Count > 0) Plugin.Logger.LogInfo($"[autorecord] no song named: {string.Join(", ", unmatched)}");
        return JsonSerializer.Serialize(new { ok = true, queued = _total, speed = _speed, unmatched });
    }

    private static bool IsPracticeSong(ModelStoryCheckpoint song)
    {
        try { return song.modelAlbum != null && !string.IsNullOrEmpty(song.modelAlbum.id); }
        catch { return false; }
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
            string level = null, soundtrack = null, album = null;
            int difficulty = -1;
            try { missing = song.isMissingSong(); } catch { }
            try { album = song.modelAlbum?.id; } catch { }
            try { difficulty = song.difficulty; } catch { }
            try { track = song.modelSoundTrack != null; soundtrack = song.modelSoundTrack?.id; } catch { }
            try { loaded = song.isMetasongLoaded(); level = loaded ? song.metaSong?.name : null; } catch { }
            list.Add(new { id = song.id, name = SafeName(song), practice = IsPracticeSong(song), album, difficulty, missing, track, soundtrack, loaded, level });
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

        if (_state == State.Opening)
        {
            Launch(now);
            return;
        }

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
                PracticeUI = null;
                _state = State.Playing;
                _since = now;
                _lastPos = p;
                _stalledSince = now;
                _stalledFrames = 0;
                Plugin.Logger.LogInfo($"[autorecord] recording {level}");
                return;
            }
            if (now - _since > 30f)
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
        if (ok && pos > _lastPos + 1f) { _lastPos = pos; _stalledSince = now; _stalledFrames = 0; }
        else _stalledFrames++;
        bool atEnd = lengthMs > 0f && _lastPos >= lengthMs - 400f;
        // Over many frames, not just seconds: one very slow frame in a busy boss fight isn't
        // the end of the song.
        bool stalled = now - _stalledSince > 6f && _stalledFrames > 60;
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
        _state = State.Opening;
        _since = Time.unscaledTime;
        _openedPractice = false;
        Launch(_since);
    }

    // Start the current job: right away from the practice screen or a finished level; from
    // the menus open the practice screen first; if it never shows, start anyway.
    private static void Launch(float now)
    {
        try
        {
            bool inLevel = NoHit.GetArcadeLevel(quiet: true)?.currentSong != null;
            if (PracticeUI != null || inLevel || now - _since > 20f)
            {
                if (PracticeUI == null && !inLevel) Plugin.Logger.LogWarning("[autorecord] the practice screen didn't show; starting anyway");
                PracticeUI = null;
                Plugin.Logger.LogInfo($"[autorecord] starting {_job.Id} ({(_job.Hard ? "hardcore" : "normal")})");
                _state = State.Starting;
                _since = now;
                var songs = new Il2CppSystem.Collections.Generic.List<ModelStoryCheckpoint>();
                songs.Add(_job.Song);
                ArcadeLevelScene.StartPractice(songs, _job.Difficulty);
                return;
            }
            if (!_openedPractice)
            {
                _openedPractice = true;
                Plugin.Logger.LogInfo("[autorecord] opening the practice screen");
                UI_Practice.loadAndCreate();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[autorecord] couldn't start {_job.Id}: {e.Message}");
            _failed++; _last = $"{_job.Id}: {e.Message}";
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

// Tells the auto-recorder when the practice screen is up (see AutoRecorder).
[HarmonyPatch]
internal static class AutoRecorderPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.start))]
    private static void PracticeOpened(UI_Practice __instance)
    {
        AutoRecorder.PracticeUI = __instance;
        if (AutoRecorder.Active) Plugin.Logger.LogInfo("[autorecord] practice screen is up");
    }
}
