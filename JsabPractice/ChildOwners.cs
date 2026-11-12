using System;
using System.Collections.Generic;
using HarmonyLib;

namespace JsabPractice;

// Which spawn made each live child actor of the spawns that draw through pooled children:
// FxSquareLine / FxSquareLineRounded / FxSquareLine2 (FxSquareAppearOptimized squares) and
// FxDNALine (FxDNALineParticle dots). The children aren't Spawns and carry no metaSpawn, and a
// parent can be gone while its children still play, so the owner is noted when createSquare
// appends the child to the parent's allSquares. Pooled children are reused: the newest owner
// wins.
[HarmonyPatch]
internal static class ChildOwners
{
    private static readonly Dictionary<IntPtr, (Actor child, int id, string type)> Owner = new();
    private static bool _failed;

    // The live children and their spawns; forgets destroyed ones.
    public static void ForEachLive(Action<Actor, (int id, string type)> fn)
    {
        List<IntPtr> gone = null;
        foreach (var (k, v) in Owner)
        {
            bool dead;
            try { dead = v.child == null || v.child.destroyed; }
            catch { dead = true; }
            if (dead) { (gone ??= new()).Add(k); continue; }
            fn(v.child, (v.id, v.type));
        }
        if (gone != null) foreach (var k in gone) Owner.Remove(k);
    }

    public static void Clear() => Owner.Clear();

    private static void Note(Spawn parent, Actor child)
    {
        try
        {
            var meta = parent?.metaSpawn;
            if (meta == null || child == null) return;
            Owner[child.Pointer] = (child, meta.idInSong, meta.modelSpawn?.id ?? "");
        }
        catch (Exception e)
        {
            if (_failed) return;
            _failed = true;
            Plugin.Logger.LogWarning($"[recorder] couldn't map child actors to spawns: {e.Message}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxSquareLine), nameof(FxSquareLine.createSquare))]
    private static void Line(FxSquareLine __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.length > 0) Note(__instance, l[l.length - 1]);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxSquareLineRounded), nameof(FxSquareLineRounded.createSquare))]
    private static void Rounded(FxSquareLineRounded __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.length > 0) Note(__instance, l[l.length - 1]);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxSquareLine2), nameof(FxSquareLine2.createSquare))]
    private static void Line2(FxSquareLine2 __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.Count > 0) Note(__instance, l[l.Count - 1]);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxDNALine), nameof(FxDNALine.createSquare))]
    private static void Dna(FxDNALine __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.Count > 0) Note(__instance, l[l.Count - 1]);
    }
}
