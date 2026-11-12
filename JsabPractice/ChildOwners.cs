using System;
using System.Collections.Generic;
using HarmonyLib;

namespace JsabPractice;

// Which spawn made each live child actor, for spawns that draw through actors they make:
// FxSquareLine / FxSquareLineRounded / FxSquareLine2 / FxLineOfRoundSpikes / Wheel
// (FxSquareAppearOptimized squares) and FxDNALine (FxDNALineParticle dots), noted when
// createSquare appends the child to the parent's allSquares; TutorialWallOfSpikes,
// BeamGroup, FxShootingStar, FxAvalanche and FxLunarWhale, whose child-making method returns
// the child (children that are Spawns carry a made-up MetaSpawn with idInSong 0, so they'd
// be tagged as spawn 0); BombShockwaveGroup, FxPlescoCheckerSquare and SpawnTrailEffect, whose
// children are whatever actors get added to a manager while their making methods run. A
// parent can be gone while its children still play, so the owner is noted at creation. Pooled
// children are reused: the newest owner wins.
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
            if (parent == null || child == null) return;
            // A parent that is itself another spawn's child (FxLineBowser's two
            // FxLineOfRoundSpikes, made with a spawn-0 MetaSpawn) passes on its own owner.
            if (Owner.TryGetValue(parent.Pointer, out var up)) { Owner[child.Pointer] = (child, up.id, up.type); return; }
            var meta = parent.metaSpawn;
            if (meta == null) return;
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

    // The same list-based owners for FxLineOfRoundSpikes and Wheel (FxSquareAppearOptimized).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxLineOfRoundSpikes), nameof(FxLineOfRoundSpikes.createSquare))]
    private static void LineOfSpikes(FxLineOfRoundSpikes __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.length > 0) Note(__instance, l[l.length - 1]);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Wheel), nameof(Wheel.createSquare))]
    private static void WheelSquare(Wheel __instance)
    {
        var l = __instance.allSquares;
        if (l != null && l.length > 0) Note(__instance, l[l.length - 1]);
    }

    // Children that are Spawns of their own (a made-up MetaSpawn with idInSong 0), returned by
    // the method that makes them: the returned actor is the child.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TutorialWallOfSpikes), nameof(TutorialWallOfSpikes.createSpike))]
    private static void WallSpike(TutorialWallOfSpikes __instance, RoundSpikes __result) => Note(__instance, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BeamGroup), nameof(BeamGroup.createSingleBeam))]
    private static void GroupBeam(BeamGroup __instance, Beam __result) => Note(__instance, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxShootingStar), nameof(FxShootingStar.createParticle))]
    private static void StarSquare(FxShootingStar __instance, Square __result) => Note(__instance, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxShootingStar), nameof(FxShootingStar.createParticle2))]
    private static void StarSpike(FxShootingStar __instance, RoundSpikes __result) => Note(__instance, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxAvalanche), nameof(FxAvalanche.createParticle2))]
    private static void AvalancheSpike(FxAvalanche __instance, RoundSpikes __result) => Note(__instance, __result);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxLunarWhale), nameof(FxLunarWhale.explodeWithAnim))]
    private static void WhaleExplosion(FxLunarWhale __instance, FxLunarWhaleExplosion __result) => Note(__instance, __result);

    // Parents with no list and no returned child: every actor added to a manager while one of
    // their methods that make children runs is theirs (a scope, innermost owner wins).
    private static readonly List<Spawn> Scope = new();
    private static void Enter(Spawn s) => Scope.Add(s);
    private static void Leave() { if (Scope.Count > 0) Scope.RemoveAt(Scope.Count - 1); }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ActorObjectManager), nameof(ActorObjectManager.add))]
    private static void Added(Actor actor)
    {
        if (Scope.Count == 0 || actor == null) return;
        var owner = Scope[Scope.Count - 1];
        if (owner != null && actor.Pointer != owner.Pointer) Note(owner, actor);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.initWithMetaspawn))]
    private static void BombInitIn(BombShockwaveGroup __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.initWithMetaspawn))]
    private static void BombInitOut() => Leave();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.update))]
    private static void BombUpdateIn(BombShockwaveGroup __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.update))]
    private static void BombUpdateOut() => Leave();

    // The parts are made from timer callbacks (strike, shockwaveGroup, shockwaveBar), not from
    // update: a recording with only the two scopes above still tagged them as spawn 0.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.strike))]
    private static void BombStrikeIn(BombShockwaveGroup __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.strike))]
    private static void BombStrikeOut() => Leave();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.shockwaveGroup))]
    private static void BombGroupIn(BombShockwaveGroup __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.shockwaveGroup))]
    private static void BombGroupOut() => Leave();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.shockwaveBar))]
    private static void BombBarIn(BombShockwaveGroup __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(BombShockwaveGroup), nameof(BombShockwaveGroup.shockwaveBar))]
    private static void BombBarOut() => Leave();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FxPlescoCheckerSquare), nameof(FxPlescoCheckerSquare.initWithMetaspawn))]
    private static void PlescoInitIn(FxPlescoCheckerSquare __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(FxPlescoCheckerSquare), nameof(FxPlescoCheckerSquare.initWithMetaspawn))]
    private static void PlescoInitOut() => Leave();

    [HarmonyPrefix]
    [HarmonyPatch(typeof(FxPlescoCheckerSquare), nameof(FxPlescoCheckerSquare.nextSquare))]
    private static void PlescoNextIn(FxPlescoCheckerSquare __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(FxPlescoCheckerSquare), nameof(FxPlescoCheckerSquare.nextSquare))]
    private static void PlescoNextOut() => Leave();

    // FxLineBowser makes its two FxLineOfRoundSpikes (left, right) in initWithMetaspawn.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(FxLineBowser), nameof(FxLineBowser.initWithMetaspawn))]
    private static void BowserIn(FxLineBowser __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(FxLineBowser), nameof(FxLineBowser.initWithMetaspawn))]
    private static void BowserOut() => Leave();

    // SpawnTrailEffect's trail copies are display clones on its targets, not actors added to a
    // manager, so this scope doesn't tag them (checked on Born Survivor): check them with
    // editor/test/check_untagged.py. Kept for any actor it does add.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SpawnTrailEffect), nameof(SpawnTrailEffect.update))]
    private static void TrailIn(SpawnTrailEffect __instance) => Enter(__instance);
    [HarmonyFinalizer]
    [HarmonyPatch(typeof(SpawnTrailEffect), nameof(SpawnTrailEffect.update))]
    private static void TrailOut() => Leave();
}
