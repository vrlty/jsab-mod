using System;
using UnityEngine;

namespace JsabPractice;

internal static class NoHit
{
    private enum State { Idle, RestartPending, FreezePending, Frozen, PreviewPending, FastForwarding, PreviewPaused }

    private static State _state = State.Idle;
    private static float _restartAt;
    private static float _previewMs;
    private static float _previewDeadline;

    // Jumping straight to a time skips hazards that spawned earlier but are still alive
    // then, so a preview starts at the earliest such spawn and fast-forwards (muted) to the
    // target. Measured against a real-time run (screenshots, 2026-10-28): music
    // playbackSpeed + Time.timeScale reproduces it; also scaling GameSpeed.speed breaks
    // some animations (black holes), and timeScale alone doesn't move the music.
    public sealed class FastForwardOptions
    {
        public float Speed = 6f;
        public bool Music = true;
        public bool GameSpeed = false;
        public bool TimeScale = true;
        public float LingerMs = 4000f;
        public float MaxLookbackMs = 25000f;
    }

    public static FastForwardOptions FastForward = new();
    private static FastForwardOptions _ff;
    private static float _ffStartPos, _ffStartTime, _ffDeadline;
    private static float _savedTimeScale, _savedGameSpeed, _savedMusicSpeed, _savedVolume;
    private static DynamicMusic _ffMusic;
    private static bool _ffSlowed;

    // Finish the last stretch at normal speed so the preview lands within a frame.
    private const float ApproachMs = 400f;

    public static bool IsFastForwarding => _state == State.FastForwarding;

    // The open pause menu, tracked by Patches so a preview can close it properly.
    internal static UI_PauseNew PauseMenu;

    public static bool IsBusy => _state != State.Idle;
    public static bool IsFrozen => _state == State.Frozen;
    public static bool IsPreviewPaused => _state == State.PreviewPaused;
    public static float PreviewMs => _previewMs;

    // In practice mode the hit to undo is held here while frozen, then dealt as a lethal one.
    private static HeroCollisionWithEnemy _heldHit;
    private static Actor _heldAttacker;

    // Called before the game applies a hit (not during i-frames). Returns whether the game
    // should go on and apply it.
    public static bool OnHit(string source, HeroCollisionWithEnemy hero, Actor attacker)
    {
        if (!Plugin.NoHitEnabled.Value)
            return true;
        // Practice: a hit is a death, so the game's own death and rewind to the start checkpoint
        // run. (Rewinding with the hero alive kept its hit blink and knockback going.)
        bool practice = Practice.Active;
        if (_state != State.Idle)
            return !(practice && _state is State.FreezePending or State.Frozen);

        if (Plugin.FreezeOnHit.Value)
        {
            // Snapshot now, before knockback moves the hero or hazards advance.
            Hitboxes.Freeze();
            _state = State.FreezePending;
            _heldHit = practice ? hero : null;
            _heldAttacker = attacker;
            Plugin.Logger.LogInfo($"Hit detected ({source}), freezing");
            return !practice;
        }

        if (practice)
        {
            Plugin.Logger.LogInfo($"Hit detected ({source}), practice: the hit is lethal");
            Practice.MakeLethal(hero);
            return true;
        }

        _state = State.RestartPending;
        _restartAt = Time.unscaledTime + Plugin.RestartDelay.Value;
        Plugin.Logger.LogInfo($"Hit detected ({source}), restarting in {Plugin.RestartDelay.Value:0.00}s");
        return true;
    }

    // Restart/unfreeze from Update rather than inside the hit callback, so we're not
    // tearing the level down from the middle of the game's own collision code.
    public static void Update()
    {
        switch (_state)
        {
            case State.RestartPending when Time.unscaledTime >= _restartAt:
                _state = State.Idle;
                RestartAfterHit(GetArcadeLevel());
                break;

            case State.Frozen:
                var arcade = GetArcadeLevel(quiet: true);
                if (arcade == null)
                {
                    // Left the level while frozen (e.g. through the pause menu).
                    _state = State.Idle;
                    Hitboxes.Unfreeze();
                    break;
                }

                if (ContinuePressed())
                {
                    _state = State.Idle;
                    Hitboxes.Unfreeze();
                    try { arcade.logicUIPause?.unpause(); }
                    catch (Exception e) { Plugin.Logger.LogError($"Unpause failed: {e}"); }
                    RestartAfterHit(arcade);
                }
                break;

            case State.PreviewPending:
                // Let the pause menu finish its closing animation before seeking.
                if (PauseMenu == null || Time.unscaledTime >= _previewDeadline)
                    SeekAndHold();
                break;

            case State.FastForwarding:
                EditorBridge.TryReadMusic(_ffMusic, out float pos, out _);
                if (!_ffSlowed && _previewMs - pos <= ApproachMs)
                {
                    RestoreSpeeds();
                    _ffSlowed = true;
                }
                bool arrived = pos >= _previewMs - 8f;
                if (arrived || Time.unscaledTime >= _ffDeadline || GetArcadeLevel(quiet: true) == null)
                {
                    EndFastForward();
                    float took = Time.unscaledTime - _ffStartTime;
                    Plugin.Logger.LogInfo($"Fast-forward {_ffStartPos:0}ms -> {pos:0}ms (target {_previewMs:0}ms) in {took:0.00}s = {(pos - _ffStartPos) / 1000f / Math.Max(took, 0.001f):0.0}x" +
                                          (arrived ? "" : " - gave up before the target"));
                    Hold(GetArcadeLevel(quiet: true));
                }
                break;

            case State.PreviewPaused:
                var level = GetArcadeLevel(quiet: true);
                if (level == null)
                {
                    _state = State.Idle;
                    break;
                }

                if (ContinuePressed())
                {
                    _state = State.Idle;
                    try { level.logicUIPause?.unpause(); }
                    catch (Exception e) { Plugin.Logger.LogError($"Unpause failed: {e}"); }
                }
                break;
        }
    }

    private static bool ContinuePressed() =>
        Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);

    // The game keeps running in the background for the editor bridge, which would
    // otherwise also skip JSAB's own pause when you switch windows mid-song.
    public static void OnFocusLost()
    {
        if (_state != State.Idle || PauseMenu != null)
            return;

        var arcade = GetArcadeLevel(quiet: true);
        var pause = arcade?.logicUIPause;
        EditorBridge.TryReadMusic(arcade?.logicEnemyCreator?.sfxView, out _, out bool playing);
        if (pause == null || !playing)
            return;

        try
        {
            if (pause.canPause())
            {
                pause.pause();
                Plugin.Logger.LogInfo("Paused because the game lost focus.");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Pause on focus loss failed: {e.Message}");
        }
    }

    // Pausing happens in LateUpdate so the game has finished this frame's update first.
    public static void LateUpdate()
    {
        if (_state != State.FreezePending)
            return;

        var arcade = GetArcadeLevel();
        var pause = arcade?.logicUIPause;
        if (pause != null)
        {
            try
            {
                pause.pauseGameWithoutMenu();
                _state = State.Frozen;
                Plugin.Logger.LogInfo("Frozen. Press Space/Enter to restart.");
                return;
            }
            catch (Exception e)
            {
                Plugin.Logger.LogError($"Freeze failed, restarting instead: {e}");
            }
        }

        _state = State.Idle;
        Hitboxes.Unfreeze();
        RestartAfterHit(arcade);
    }

    // Hot reload for the level editor: re-read the custom file for the song being played
    // and restart it.
    public static void ReloadCustomLevel()
    {
        var arcade = GetArcadeLevel();
        var song = arcade?.currentSong;
        if (song == null)
            return;

        if (!LevelImport.TryApply(song, restoreIfMissing: true))
            Plugin.Logger.LogInfo($"Reload: nothing changed for '{LevelDump.LevelName(song)}' (no custom file and nothing to restore).");

        if (_state == State.FastForwarding)
            EndFastForward();
        _state = State.Idle;
        Hitboxes.Unfreeze();
        try { arcade.logicUIPause?.unpause(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Unpause before reload failed: {e.Message}"); }
        Restart(arcade);
    }

    // Live preview from the editor: reload the custom file for the song being played, then
    // (once any pause menu has closed) jump to timeMs and hold there until Space/Enter, so
    // nothing plays while you're still in the editor.
    public static string PreviewFrom(ActorArcadeLevelLogic arcade, float timeMs, FastForwardOptions ff = null)
    {
        _ff = ff ?? FastForward;
        if (_state == State.FastForwarding)
            EndFastForward();
        var song = arcade.currentSong;
        string name = LevelDump.LevelName(song);
        LevelImport.TryApply(song, restoreIfMissing: true);

        bool heldByUs = _state is State.Frozen or State.PreviewPaused;
        _state = State.Idle;
        Hitboxes.Unfreeze();

        if (timeMs <= 0f)
        {
            CloseOrUnpause(arcade, heldByUs);
            Restart(arcade);
            return System.Text.Json.JsonSerializer.Serialize(new { ok = true, level = name, timeMs = 0f, restarted = true });
        }

        CloseOrUnpause(arcade, heldByUs);
        _previewMs = timeMs;
        _previewDeadline = Time.unscaledTime + 3f;
        _state = State.PreviewPending;
        return System.Text.Json.JsonSerializer.Serialize(new { ok = true, level = name, timeMs, held = true });
    }

    private static void CloseOrUnpause(ActorArcadeLevelLogic arcade, bool heldByUs)
    {
        try
        {
            if (PauseMenu != null)
                PauseMenu.closeFromResume();
            else if (heldByUs)
                arcade.logicUIPause?.unpause();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Resuming before preview failed: {e.Message}");
        }
    }

    // Seek with the spawn creator's own skip-ahead (music position is in ms) to the earliest
    // spawn still alive at the target, fast-forward to the target, then pause.
    private static void SeekAndHold()
    {
        _state = State.Idle;
        var arcade = GetArcadeLevel();
        var creator = arcade?.logicEnemyCreator;
        var music = creator?.sfxView;
        if (!EditorBridge.TryReadMusic(music, out float before, out _))
        {
            Plugin.Logger.LogWarning("Preview: the song isn't loaded yet; try again in a moment.");
            return;
        }

        float start = EarliestLiveStart(arcade.currentSong?.metaSong, _previewMs, _ff);
        creator.destroyAllEnemies();
        creator.putMusicTo(start);
        EditorBridge.TryReadMusic(music, out float after, out _);
        Plugin.Logger.LogInfo($"Preview '{LevelDump.LevelName(arcade.currentSong)}': music {before:0}ms -> {after:0}ms, target {_previewMs:0}ms");

        if (_previewMs - start > 100f && _ff.Speed > 1f)
            BeginFastForward(music, start);
        else
            Hold(arcade);
    }

    // The earliest warning-start among spawns that may still be on screen at the target.
    // Hazards can outlive their outro (spikes flying off), hence the linger allowance.
    private static float EarliestLiveStart(MetaSong meta, float target, FastForwardOptions ff)
    {
        var list = meta?.allSpawnsInOrder;
        if (list == null)
            return target;

        float earliest = target;
        for (int i = 0; i < list.Count; i++)
        {
            var tl = list[i]?.getTimeline();
            if (tl == null) continue;
            float start = tl.timeWithWarning();
            if (start >= earliest) continue;
            float end = Math.Max(tl.time(), tl.timeAfterOutro()) + ff.LingerMs;
            if (end >= target) earliest = start;
        }
        return Math.Max(0f, Math.Max(earliest, target - ff.MaxLookbackMs));
    }

    private static void BeginFastForward(DynamicMusic music, float start)
    {
        _ffMusic = music;
        _ffStartPos = start;
        _ffStartTime = Time.unscaledTime;
        _ffDeadline = Time.unscaledTime + (_previewMs - start) / 1000f / _ff.Speed * 3f + 5f;

        _savedTimeScale = Time.timeScale;
        _savedGameSpeed = GameSpeed.speed;
        _savedMusicSpeed = music.playbackSpeed;
        _savedVolume = AudioListener.volume;
        if (_ff.TimeScale) Time.timeScale = _ff.Speed;
        if (_ff.GameSpeed) GameSpeed.speed = _ff.Speed;
        if (_ff.Music) music.playbackSpeed = _ff.Speed;
        AudioListener.volume = 0f;
        _ffSlowed = false;
        _state = State.FastForwarding;
    }

    private static void RestoreSpeeds()
    {
        Time.timeScale = _savedTimeScale;
        GameSpeed.speed = _savedGameSpeed;
        try { if (_ffMusic != null) _ffMusic.playbackSpeed = _savedMusicSpeed; }
        catch { /* music gone */ }
    }

    private static void EndFastForward()
    {
        if (!_ffSlowed)
            RestoreSpeeds();
        AudioListener.volume = _savedVolume;
        _ffMusic = null;
        _state = State.Idle;
    }

    private static void Hold(ActorArcadeLevelLogic arcade)
    {
        if (arcade == null)
            return;
        try
        {
            arcade.logicUIPause.pauseGameWithoutMenu();
            _state = State.PreviewPaused;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Holding the preview failed, playing right away: {e.Message}");
        }
    }

    internal static ActorArcadeLevelLogic GetArcadeLevel(bool quiet = false)
    {
        try
        {
            if (MultiplayerRoom.Instance != null)
            {
                if (!quiet) Plugin.Logger.LogWarning("Multiplayer room is active; leaving the level alone.");
                return null;
            }

            var logic = ActorNormalLevelLogic.getNormalLevelLogic();
            var arcade = logic?.TryCast<ActorArcadeLevelLogic>();
            if (arcade == null && !quiet)
            {
                string kind = logic == null ? "none" : logic.GetIl2CppType().FullName;
                Plugin.Logger.LogInfo($"Not in an arcade/practice level (level logic: {kind}).");
            }
            return arcade;
        }
        catch (Exception e)
        {
            if (!quiet) Plugin.Logger.LogError($"Level lookup failed: {e}");
            return null;
        }
    }

    // After a freeze: deal the held practice hit (a death, then the game's rewind), else restart.
    private static void RestartAfterHit(ActorArcadeLevelLogic arcade)
    {
        var held = _heldHit;
        _heldHit = null;
        if (held != null && arcade != null && Practice.Active)
            Practice.Kill(held, _heldAttacker);
        else
            Restart(arcade);
        _heldAttacker = null;
    }

    internal static void Restart(ActorArcadeLevelLogic arcade)
    {
        if (arcade == null)
            return;

        try
        {
            arcade.restartSong();
            Plugin.Logger.LogInfo("restartSong() called.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Restart failed: {e}");
        }
    }
}
