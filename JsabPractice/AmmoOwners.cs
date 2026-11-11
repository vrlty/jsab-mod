using System;
using System.Collections.Generic;
using HarmonyLib;

namespace JsabPractice;

// Which spawn fired each live ammo (ExplodingSpikes' RedAmmo burst, ...). Ammo isn't a Spawn
// and has no metaSpawn, so the recorder couldn't name its spawn: a weapon's shoot adds the
// new ammo to GameScene.ammoManager.actorList (WeaponComponent.shootAmmo), so the entries
// added during a shoot by a spawn's weapon are that spawn's.
[HarmonyPatch]
internal static class AmmoOwners
{
    // Ammo pointer -> (MetaSpawn.idInSong, spawn type) of the spawn whose weapon shot it.
    private static readonly Dictionary<IntPtr, (int id, string type)> Owner = new();
    private static int _depth, _countBefore;
    private static (int id, string type)? _shooter;
    private static bool _failed;

    public static bool TryGet(IntPtr ammo, out (int id, string type) info) => Owner.TryGetValue(ammo, out info);

    // Forget ammo that's no longer live (pooled ammo is reused under the same pointer).
    public static void Prune(HashSet<IntPtr> live)
    {
        if (Owner.Count == 0) return;
        List<IntPtr> gone = null;
        foreach (var k in Owner.Keys) if (!live.Contains(k)) (gone ??= new()).Add(k);
        if (gone != null) foreach (var k in gone) Owner.Remove(k);
    }

    public static void Clear() => Owner.Clear();

    private static Il2CppSystem.Collections.Generic.List<Actor> AmmoList() =>
        GameScene.instance?.ammoManager?.actorList;

    private static void Before(WeaponComponent weapon)
    {
        if (_depth++ > 0) return;
        _shooter = null;
        try
        {
            var meta = weapon?.actor?.TryCast<Spawn>()?.metaSpawn;
            var list = AmmoList();
            if (meta == null || list == null) return;
            _shooter = (meta.idInSong, meta.modelSpawn?.id ?? "");
            _countBefore = list.Count;
        }
        catch (Exception e) { Fail(e); }
    }

    private static void After()
    {
        if (--_depth > 0 || _shooter == null) return;
        try
        {
            var list = AmmoList();
            if (list == null) return;
            for (int i = Math.Max(0, _countBefore); i < list.Count; i++)
                if (list[i] != null) Owner[list[i].Pointer] = _shooter.Value;
        }
        catch (Exception e) { Fail(e); }
        finally { _shooter = null; }
    }

    private static void Fail(Exception e)
    {
        if (_failed) return;
        _failed = true;
        Plugin.Logger.LogWarning($"[recorder] couldn't map ammo to spawns: {e.Message}");
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WeaponComponent), nameof(WeaponComponent.shoot))]
    private static void ShootPrefix(WeaponComponent __instance) => Before(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(WeaponComponent), nameof(WeaponComponent.shoot))]
    private static void ShootFinalizer() => After();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WeaponCircleComponent), nameof(WeaponCircleComponent.shoot))]
    private static void CircleShootPrefix(WeaponCircleComponent __instance) => Before(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(WeaponCircleComponent), nameof(WeaponCircleComponent.shoot))]
    private static void CircleShootFinalizer() => After();
}
