using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace JsabPractice;

// Practice mode: pick one of the level's own checkpoints to start from (and optionally one to
// end at). Deaths and no-hit hits send you back to the start checkpoint, passing a later
// checkpoint doesn't move your respawn, and reaching the end checkpoint loops back.
//
// It drives the game's own checkpoint system rather than saving states. Passing a checkpoint
// (LogicCheckPointCheck.completeCheckPoint) stores lastCheckPoint (music ms) and the camera,
// switches the background, and for Tokyo Skies and package songs snapshots the hazards still
// on screen (LogicCloneObjectsOnRewind.listToClone, refilled in place). A death
// (LogicRewindMusic.rewindMusic) rewinds to lastCheckPoint. So practice keeps per-checkpoint
// copies of the camera and snapshot, and sets all three up for the start checkpoint before
// every rewind. The level start rewinds like a death before the first checkpoint, with the
// opening background (remembered when the first checkpoint is reached) put back.
[HarmonyPatch]
internal static class Practice
{
    internal sealed class Checkpoint
    {
        public int Ms;
        public MetaSpawn Spawn; // null for the level start
    }

    // [0] = level start; the level's last checkpoint (where it ends) isn't listed.
    internal static readonly List<Checkpoint> Points = new();
    internal static int StartIndex;
    internal static int EndIndex = -1; // -1 = play to the end of the level

    private static IntPtr _metaSong;
    private static string _level;
    private static readonly Dictionary<int, Vector2> _cameraAt = new();
    private static readonly Dictionary<int, List<MetaSpawn>> _clonesAt = new();
    private static int _completing = -1;
    private static ModelBackGround _startBackground;
    private static bool _rewindRequested;
    private static string _banner;
    private static float _bannerUntil;

    internal static bool InLevel => Points.Count > 0;
    internal static bool Active => Plugin.PracticeEnabled.Value && InLevel && MultiplayerRoom.Instance == null;
    internal static string Banner => Time.unscaledTime < _bannerUntil ? _banner : null;

    internal static string Describe(int index) =>
        index < 0 ? "End of level"
        : index == 0 ? "Start of level"
        : $"Checkpoint {index}  ({FormatTime(Points[index].Ms)})";

    internal static string FormatTime(float ms) => $"{(int)(ms / 60000)}:{ms % 60000 / 1000f:00.0}";

    public static void Update()
    {
        var arcade = NoHit.GetArcadeLevel(quiet: true);
        Refresh(arcade);
        if (arcade == null)
            return;

        if (_rewindRequested)
        {
            _rewindRequested = false;
            GoToStart(arcade);
        }
    }

    // Keeps the checkpoint list in step with the level being played. The selection survives
    // restarts and reloads of the same level.
    private static void Refresh(ActorArcadeLevelLogic arcade)
    {
        MetaSong meta = null;
        try { meta = arcade?.currentSong?.metaSong; }
        catch { /* level tearing down */ }

        if (meta == null)
        {
            if (Points.Count > 0) Points.Clear();
            _metaSong = IntPtr.Zero;
            return;
        }
        if (meta.Pointer == _metaSong)
            return;

        _metaSong = meta.Pointer;
        string level = LevelDump.LevelName(arcade.currentSong);
        Points.Clear();
        Points.Add(new Checkpoint { Ms = 0 });
        try
        {
            var all = meta.getAllCheckPoint();
            for (int i = 0; all != null && i < all.Count; i++)
            {
                var spawn = all[i];
                if (spawn == null || spawn.isLastCheckPoint(meta))
                    continue;
                int ms = (int)spawn.getTimeline().timeInMs.getSimpleValueAsNumber();
                if (ms > 0)
                    Points.Add(new Checkpoint { Ms = ms, Spawn = spawn });
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[practice] reading checkpoints failed: {e.Message}");
        }
        Points.Sort((a, b) => a.Ms.CompareTo(b.Ms));

        if (level != _level)
        {
            _level = level;
            StartIndex = 0;
            EndIndex = -1;
            _cameraAt.Clear();
            _clonesAt.Clear();
            _startBackground = null;
        }
        StartIndex = Math.Min(StartIndex, Points.Count - 1);
        if (EndIndex >= Points.Count || EndIndex <= StartIndex) EndIndex = -1;
        Plugin.Logger.LogInfo($"[practice] '{level}': {Points.Count - 1} checkpoint(s) to practice from");
    }

    internal static void CycleStart(int step)
    {
        StartIndex = (StartIndex + step + Points.Count) % Points.Count;
        if (EndIndex >= 0 && EndIndex <= StartIndex) EndIndex = -1;
    }

    // The end is one of the checkpoints after the start, or the end of the level.
    internal static void CycleEnd(int step)
    {
        var options = new List<int>();
        for (int i = StartIndex + 1; i < Points.Count; i++) options.Add(i);
        options.Add(-1);
        int at = Math.Max(0, options.IndexOf(EndIndex));
        EndIndex = options[(at + step + options.Count) % options.Count];
    }

    // Back to the start checkpoint now (a hit in no-hit mode, or a new start picked in the menu).
    internal static void GoToStart(ActorArcadeLevelLogic arcade)
    {
        if (StartIndex == 0)
        {
            NoHit.Restart(arcade);
            return;
        }
        try { arcade.logicRewindMusic?.rewindMusic(); }
        catch (Exception e) { Plugin.Logger.LogError($"[practice] rewind failed: {e}"); }
    }

    // What NoHit does after a hit: returns false when practice isn't on, so it restarts the song.
    internal static bool RestartAfterHit(ActorArcadeLevelLogic arcade)
    {
        if (!Active || arcade == null)
            return false;
        GoToStart(arcade);
        return true;
    }

    private static void Show(string text)
    {
        _banner = text;
        _bannerUntil = Time.unscaledTime + 2f;
    }

    private sealed class Saved
    {
        public int Last;
        public Vector2 Camera;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicCheckPointCheck), nameof(LogicCheckPointCheck.completeCheckPoint))]
    private static void CheckpointReaching(LogicCheckPointCheck __instance, CheckPoint c, out Saved __state)
    {
        __state = null;
        try
        {
            var cam = __instance.cameraAtLastCheckpoint;
            __state = new Saved { Last = __instance.lastCheckPoint, Camera = cam == null ? default : new Vector2(cam.x, cam.y) };
            _completing = c?.posInMusic ?? -1;
            // The first checkpoint of a run still shows the level's opening background.
            if (__instance.lastCheckPoint == 0 && _startBackground == null)
                _startBackground = (__instance.fxBackground ?? __instance.actorNormalLevel?.fxBackground)?.modelBackGround;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[practice] checkpoint prefix: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LogicCheckPointCheck), nameof(LogicCheckPointCheck.completeCheckPoint))]
    private static void CheckpointReached(LogicCheckPointCheck __instance, CheckPoint c, Saved __state)
    {
        int ms = _completing;
        _completing = -1;
        try
        {
            // The game skips checkpoints while it's rewinding (enabled = false).
            if (c == null || __state == null || __instance.lastCheckPoint != ms || ms == __state.Last)
                return;
            var cam = __instance.cameraAtLastCheckpoint;
            if (cam != null)
                _cameraAt[ms] = new Vector2(cam.x, cam.y);

            if (!Active || ms <= Points[StartIndex].Ms)
                return;

            // Keep the respawn at the start checkpoint. The background change stays: that's
            // how the level looks from here on.
            __instance.lastCheckPoint = __state.Last;
            if (cam != null) { cam.x = __state.Camera.x; cam.y = __state.Camera.y; }

            if (EndIndex >= 0 && ms >= Points[EndIndex].Ms)
            {
                Plugin.Logger.LogInfo($"[practice] reached the end checkpoint ({FormatTime(ms)}), back to {Describe(StartIndex)}");
                Show("Section cleared!");
                _rewindRequested = true;
            }
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[practice] checkpoint postfix: {e.Message}"); }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LogicCloneObjectsOnRewind), nameof(LogicCloneObjectsOnRewind.onCheckpoint))]
    private static void HazardsSnapshotted(LogicCloneObjectsOnRewind __instance)
    {
        if (_completing < 0) return;
        try
        {
            var list = __instance.listToClone;
            var copy = new List<MetaSpawn>();
            for (int i = 0; list != null && i < list.Count; i++)
                copy.Add(list[i]);
            _clonesAt[_completing] = copy;
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[practice] snapshot copy: {e.Message}"); }
    }

    // Every rewind in practice goes to the start checkpoint, set up as if it was the last one
    // reached: deaths, no-hit hits, the section loop and jumps from the menu.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicRewindMusic), nameof(LogicRewindMusic.rewindMusic))]
    private static bool Rewinding(LogicRewindMusic __instance)
    {
        if (!Active || __instance._isRewinding)
            return true;
        try
        {
            var level = __instance.actorNormalGame;
            var logic = level.logicCheckPoint;
            var start = Points[StartIndex];
            logic.lastCheckPoint = start.Ms;

            var cam = logic.cameraAtLastCheckpoint;
            if (StartIndex == 0) _cameraAt[0] = Vector2.zero; // as the game's own restart sets it
            if (cam != null && _cameraAt.TryGetValue(start.Ms, out var at)) { cam.x = at.x; cam.y = at.y; }

            var background = StartIndex == 0 ? _startBackground : start.Spawn?.metaParams?.getBackGround();
            var fx = logic.fxBackground ?? level.fxBackground;
            if (background != null && fx != null)
                fx.setBackground(background);

            var clones = level.logicCloneOnRewind?.listToClone;
            if (clones != null)
            {
                clones.Clear();
                if (_clonesAt.TryGetValue(start.Ms, out var saved))
                    foreach (var s in saved) clones.Add(s);
            }

            __instance.infiniteRewind = true;
            Plugin.Logger.LogInfo($"[practice] rewinding to {Describe(StartIndex)}" +
                                  (_cameraAt.ContainsKey(start.Ms) ? "" : " (not reached yet this level: camera left as is)"));
        }
        catch (Exception e) { Plugin.Logger.LogError($"[practice] rewind setup failed: {e}"); }
        return true;
    }
}
