using HarmonyLib;

namespace JsabPractice;

[HarmonyPatch]
internal static class Patches
{
    // The game's central "a player took damage" event; the No Damage ribbon listens to this.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RegisterManager), nameof(RegisterManager.RegisterHit))]
    private static void RegisterHit(ModelPlayer pModelPlayer, float currentHp)
    {
        if (Plugin.Diagnostics.Value)
            Plugin.Logger.LogInfo($"[diag] RegisterHit hp={currentHp}");
        NoHit.OnHit("RegisterHit");
    }

    // Logged only, to confirm it lines up with RegisterHit (and isn't called while invincible).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByEnemy))]
    private static void HitByEnemy()
    {
        if (Plugin.Diagnostics.Value)
            Plugin.Logger.LogInfo("[diag] HeroCollisionWithEnemy.hitByEnemy");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionCircleComponent), nameof(CollisionCircleComponent.attackTargets))]
    private static void AttackCircle(float x, float y, float radius)
    {
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleCircle(x, y, radius);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets),
        typeof(Actor), typeof(VectorFlash<CollisionCircleComponent>), typeof(float), typeof(float), typeof(float), typeof(float), typeof(float))]
    private static void AttackRect(float x, float y, float scaleX, float scaleY, float rot)
    {
        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.SampleRect(x, y, scaleX, scaleY, rot);
    }
}
