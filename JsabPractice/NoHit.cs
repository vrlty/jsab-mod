using System;
using UnityEngine;

namespace JsabPractice;

internal static class NoHit
{
    private static float _restartAt = -1f;

    public static void OnHit(string source)
    {
        if (!Plugin.NoHitEnabled.Value || _restartAt >= 0f)
            return;

        _restartAt = Time.unscaledTime + Plugin.RestartDelay.Value;
        Plugin.Logger.LogInfo($"Hit detected ({source}), restarting in {Plugin.RestartDelay.Value:0.00}s");
    }

    // Restart from Update rather than inside the hit callback, so we're not tearing the
    // level down from the middle of the game's own collision code.
    public static void Update()
    {
        if (_restartAt < 0f || Time.unscaledTime < _restartAt)
            return;

        _restartAt = -1f;
        Restart();
    }

    private static void Restart()
    {
        try
        {
            if (MultiplayerRoom.Instance != null)
            {
                Plugin.Logger.LogWarning("Multiplayer room is active; not restarting.");
                return;
            }

            var logic = ActorNormalLevelLogic.getNormalLevelLogic();
            var arcade = logic?.TryCast<ActorArcadeLevelLogic>();
            if (arcade == null)
            {
                string kind = logic == null ? "none" : logic.GetIl2CppType().FullName;
                Plugin.Logger.LogInfo($"Not in an arcade/practice level (level logic: {kind}); no restart.");
                return;
            }

            arcade.restartSong();
            Plugin.Logger.LogInfo("restartSong() called.");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Restart failed: {e}");
        }
    }
}
