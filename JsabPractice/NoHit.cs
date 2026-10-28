using System;
using UnityEngine;

namespace JsabPractice;

internal static class NoHit
{
    private enum State { Idle, RestartPending, FreezePending, Frozen, PreviewPending, PreviewPaused }

    private static State _state = State.Idle;
    private static float _restartAt;
    private static float _previewMs;
    private static float _previewDeadline;

    // The open pause menu, tracked by Patches so a preview can close it properly.
    internal static UI_PauseNew PauseMenu;

    public static bool IsFrozen => _state == State.Frozen;
    public static bool IsPreviewPaused => _state == State.PreviewPaused;
    public static float PreviewMs => _previewMs;

    public static void OnHit(string source)
    {
        if (!Plugin.NoHitEnabled.Value || _state != State.Idle)
            return;

        if (Plugin.FreezeOnHit.Value)
        {
            // Snapshot now, before knockback moves the hero or hazards advance.
            Hitboxes.Freeze();
            _state = State.FreezePending;
            Plugin.Logger.LogInfo($"Hit detected ({source}), freezing");
            return;
        }

        _state = State.RestartPending;
        _restartAt = Time.unscaledTime + Plugin.RestartDelay.Value;
        Plugin.Logger.LogInfo($"Hit detected ({source}), restarting in {Plugin.RestartDelay.Value:0.00}s");
    }

    // Restart/unfreeze from Update rather than inside the hit callback, so we're not
    // tearing the level down from the middle of the game's own collision code.
    public static void Update()
    {
        switch (_state)
        {
            case State.RestartPending when Time.unscaledTime >= _restartAt:
                _state = State.Idle;
                Restart(GetArcadeLevel());
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
                    Restart(arcade);
                }
                break;

            case State.PreviewPending:
                // Let the pause menu finish its closing animation before seeking.
                if (PauseMenu == null || Time.unscaledTime >= _previewDeadline)
                    SeekAndHold();
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
        var music = arcade?.logicEnemyCreator?.sfxView;
        if (pause == null || music == null || !music.isPlaying)
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
        Restart(arcade);
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

        _state = State.Idle;
        Hitboxes.Unfreeze();
        try { arcade.logicUIPause?.unpause(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Unpause before reload failed: {e.Message}"); }
        Restart(arcade);
    }

    // Live preview from the editor: reload the custom file for the song being played, then
    // (once any pause menu has closed) jump to timeMs and hold there until Space/Enter, so
    // nothing plays while you're still in the editor.
    public static string PreviewFrom(ActorArcadeLevelLogic arcade, float timeMs)
    {
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

    // Seek with the spawn creator's own skip-ahead (music position is in ms), then pause.
    private static void SeekAndHold()
    {
        _state = State.Idle;
        var arcade = GetArcadeLevel();
        var creator = arcade?.logicEnemyCreator;
        var music = creator?.sfxView;
        if (music == null)
            return;

        float before = music.position;
        creator.destroyAllEnemies();
        creator.putMusicTo(_previewMs);
        Plugin.Logger.LogInfo($"Preview '{LevelDump.LevelName(arcade.currentSong)}': music {before:0}ms -> {music.position:0}ms (asked {_previewMs:0}ms)");

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

    private static void Restart(ActorArcadeLevelLogic arcade)
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
