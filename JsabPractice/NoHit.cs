using System;
using UnityEngine;

namespace JsabPractice;

internal static class NoHit
{
    private enum State { Idle, RestartPending, FreezePending, Frozen }

    private static State _state = State.Idle;
    private static float _restartAt;

    public static bool IsFrozen => _state == State.Frozen;

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

                if (Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    _state = State.Idle;
                    Hitboxes.Unfreeze();
                    try { arcade.logicUIPause?.unpause(); }
                    catch (Exception e) { Plugin.Logger.LogError($"Unpause failed: {e}"); }
                    Restart(arcade);
                }
                break;
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

    // Live preview from the editor: reload the custom file for the song being played and
    // jump to timeMs using the spawn creator's own seek (the developers' skip-ahead).
    public static string PreviewFrom(ActorArcadeLevelLogic arcade, float timeMs)
    {
        var song = arcade.currentSong;
        string name = LevelDump.LevelName(song);
        LevelImport.TryApply(song, restoreIfMissing: true);

        _state = State.Idle;
        Hitboxes.Unfreeze();
        try { arcade.logicUIPause?.unpause(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Unpause before preview failed: {e.Message}"); }

        var creator = arcade.logicEnemyCreator;
        var music = creator?.sfxView;
        if (timeMs <= 0f || creator == null || music == null)
        {
            Restart(arcade);
            return System.Text.Json.JsonSerializer.Serialize(new { ok = true, level = name, timeMs = 0f, restarted = true });
        }

        float before = music.position;
        creator.destroyAllEnemies();
        creator.putMusicTo(EditorBridge.FromMs(music, timeMs));
        float after = music.position;
        Plugin.Logger.LogInfo($"Preview '{name}' from {timeMs:0}ms: music position {before:0.###} -> {after:0.###} (length {music.length}, treating units as {(EditorBridge.MusicUsesMs(music) ? "ms" : "seconds")})");

        return System.Text.Json.JsonSerializer.Serialize(new
        {
            ok = true,
            level = name,
            timeMs,
            positionBefore = EditorBridge.ToMs(music, before),
            positionAfter = EditorBridge.ToMs(music, after),
        });
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
