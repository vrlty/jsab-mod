using HarmonyLib;

namespace JsabPractice;

[HarmonyPatch]
internal static class Patches
{
    // Ribbon stat event. Not raised in practice/playlist mode (only the hitByEnemy path is),
    // so it's kept for diagnostics only.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RegisterManager), nameof(RegisterManager.RegisterHit))]
    private static void RegisterHit(ModelPlayer pModelPlayer, float currentHp)
    {
        if (Plugin.Diagnostics.Value)
            Plugin.Logger.LogInfo($"[diag] RegisterHit hp={currentHp}");
    }

    // Collision callback on the hero. Checked in a prefix so i-frames (dash, respawn,
    // post-hit) are read before the hit itself starts a new invincibility window.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByEnemy))]
    private static bool HitByEnemy(HeroCollisionWithEnemy __instance, Actor h)
    {
        Practice.LastHit = __instance;
        if (Practice.Killing)
            return true;
        if (Practice.IsRewinding())
        {
            Plugin.Logger.LogInfo("Hit ignored: rewinding");
            return false;
        }
        // An editor preview fast-forwards with nobody steering; don't let that kill the player.
        // Nor while recording unattended: the run has to reach the end of the song.
        if (NoHit.IsFastForwarding || AutoRecorder.Active)
            return false;

        bool invincible = __instance.hero?.heroCooldown?.isInvincible() ?? false;
        if (Plugin.Diagnostics.Value)
            Plugin.Logger.LogInfo($"[diag] hitByEnemy invincible={invincible}");
        return invincible || NoHit.OnHit("hitByEnemy", __instance, h);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.die))]
    private static void HeroDied()
    {
        Plugin.Logger.LogInfo("Player died (game)");
        Recorder.OnPlayerDied();
    }

    // Where each spawn lands (random positions resolved), for the motion recording.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MetaSpawn), nameof(MetaSpawn.createSpawn))]
    private static void SpawnCreated(MetaSpawn __instance, Spawn __result) => Recorder.OnSpawnCreated(__instance, __result);

    // One game tick, for the motion recording: JSB_Init.FixedUpdate runs MainGame.update (and
    // through it GameScene.update, which moves every hazard) once per FixedUpdate. GameScene.update
    // itself can't be hooked: a postfix on it never ran (the native code inlines it).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Assets.Plugins.FlashLoader.JSB_Init), nameof(Assets.Plugins.FlashLoader.JSB_Init.FixedUpdate))]
    private static void GameTicked() => Recorder.OnSceneTick();

    [HarmonyPostfix]
    [HarmonyPatch(typeof(LogicRewindMusic), nameof(LogicRewindMusic.rewindMusic))]
    private static void GameRewound() => Plugin.Logger.LogInfo("Game rewound to the last checkpoint");

    // After a rewind or a jump, the spawner skips every spawn whose time - warning is behind
    // the music position it reads right then. Logs where it landed, so a jump that skips the
    // wrong spawns shows up.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(LogicSpawnCreator), nameof(LogicSpawnCreator.skipCallbackToCurrentPosition))]
    private static void SpawnerRepositioned(LogicSpawnCreator __instance)
    {
        try
        {
            float musicMs = (__instance.sfxView?._soundChannel?.position ?? -1f) * 1000f;
            var spawns = __instance.metaSong?.allSpawnsInOrder;
            int next = __instance.indexOfNext;
            string nextAt = "end";
            if (spawns != null && next < spawns.Count)
            {
                var t = spawns[next].getTimeline();
                nextAt = $"{t.timeInMs.getSimpleValueAsNumber() - t.warningTime.getSimpleValueAsNumber():0}ms";
            }
            Plugin.Logger.LogInfo($"[spawner] repositioned: music at {musicMs:0}ms, next spawn #{next} of {spawns?.Count} (time - warning {nextAt})");
        }
        catch (System.Exception e)
        {
            Plugin.Logger.LogWarning($"[spawner] reposition log failed: {e.Message}");
        }
    }

    // Track the pause menu so an editor preview can close it the way its Resume button does.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.start))]
    private static void PauseMenuOpened(UI_PauseNew __instance) => NoHit.PauseMenu = __instance;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UI_PauseNew), nameof(UI_PauseNew.destroy))]
    private static void PauseMenuClosed(UI_PauseNew __instance)
    {
        if (NoHit.PauseMenu != null && NoHit.PauseMenu.Pointer == __instance.Pointer)
            NoHit.PauseMenu = null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionCircleComponent), nameof(CollisionCircleComponent.attackTargets))]
    private static void AttackCircle(Actor attacker, float x, float y, float radius)
    {
        Hitboxes.AddCircle(x, y, radius);
        Recorder.OnCircle(attacker, x, y, radius);
        if (SpawnTrace.Tracing)
            SpawnTrace.OnShape(attacker, $"circle({x:0},{y:0} r{radius:0})");
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleCircle(x, y, radius);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets),
        typeof(Actor), typeof(VectorFlash<CollisionCircleComponent>), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float))]
    private static void AttackRect(Actor attacker, float x, float y, float scaleX, float scaleY, float rot)
    {
        Hitboxes.AddRect(x, y, scaleX, scaleY, rot);
        Recorder.OnRect(attacker, x, y, scaleX, scaleY, rot);
        if (SpawnTrace.Tracing)
            SpawnTrace.OnShape(attacker, $"rect({x:0},{y:0} {scaleX:0}x{scaleY:0} rot{rot:0})");
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleRect(x, y, scaleX, scaleY, rot);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets),
        typeof(Actor), typeof(VectorFlash<CollisionCircleComponent>), typeof(RectangleLines))]
    private static void AttackRectLines(Actor attacker, RectangleLines rectLines)
    {
        if (rectLines == null)
            return;
        Hitboxes.AddQuad(rectLines.topLeft, rectLines.topRight, rectLines.bottomRight, rectLines.bottomLeft);
        Recorder.OnQuad(attacker, rectLines.topLeft, rectLines.topRight, rectLines.bottomRight, rectLines.bottomLeft);
        if (SpawnTrace.Tracing)
            SpawnTrace.OnShape(attacker, $"quad(tl {rectLines.topLeft} br {rectLines.bottomRight})");
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleRectLines(rectLines);
    }
}
