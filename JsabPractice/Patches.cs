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
    private static void HitByEnemy(HeroCollisionWithEnemy __instance)
    {
        bool invincible = __instance.hero?.heroCooldown?.isInvincible() ?? false;
        if (Plugin.Diagnostics.Value)
            Plugin.Logger.LogInfo($"[diag] hitByEnemy invincible={invincible}");
        if (!invincible)
            NoHit.OnHit("hitByEnemy");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionCircleComponent), nameof(CollisionCircleComponent.attackTargets))]
    private static void AttackCircle(float x, float y, float radius)
    {
        Hitboxes.AddCircle(x, y, radius);
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleCircle(x, y, radius);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets),
        typeof(Actor), typeof(VectorFlash<CollisionCircleComponent>), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float))]
    private static void AttackRect(float x, float y, float scaleX, float scaleY, float rot)
    {
        Hitboxes.AddRect(x, y, scaleX, scaleY, rot);
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleRect(x, y, scaleX, scaleY, rot);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets),
        typeof(Actor), typeof(VectorFlash<CollisionCircleComponent>), typeof(RectangleLines))]
    private static void AttackRectLines(RectangleLines rectLines)
    {
        if (rectLines == null)
            return;
        Hitboxes.AddQuad(rectLines.topLeft, rectLines.topRight, rectLines.bottomRight, rectLines.bottomLeft);
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleRectLines(rectLines);
    }
}
