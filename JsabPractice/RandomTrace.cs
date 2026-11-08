using System;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Runtime;

namespace JsabPractice;

// Development aid for the editor's simulator: logs what the editor needs to reproduce the
// game's seeded randomness. For each RoundSpikes.setRandomPos (spikes with no position set):
// the spawn's idInSong and randomOffset, MyMath.roomSeed, and the position before and after.
// Off unless Debug/TraceRandom is on; capped per run.
//
// MyMath itself can't be hooked or touched through its interop wrapper: that runs its static
// constructor, which throws (SEHException in il2cpp_runtime_class_init) at any time, and
// from Plugin.Load it took the whole plugin down. So roomSeed is read straight from the
// class's static fields (Il2CppClass.static_fields at +0xB8; roomSeed at +0x18, after
// isInit, tabCos, tabSin), and the hooks are installed late (Install, from PracticeRunner).
[HarmonyPatch]
internal static class RandomTrace
{
    private const int MaxLines = 4000;
    private static int _lines;
    private static bool _installed;
    private static IntPtr _myMath;
    private static int _lastSeed = int.MinValue;

    // Patch once, when tracing is on; failure only disables the trace.
    internal static void Install()
    {
        if (_installed || !Plugin.TraceRandom.Value || Plugin.Harmony == null)
            return;
        _installed = true;
        try
        {
            Plugin.Harmony.PatchAll(typeof(RandomTrace));
            Plugin.Logger.LogInfo($"[random] tracing on; roomSeed {RoomSeed()}");
            LogUnityRandomSamples();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"[random] couldn't install the trace: {e}");
        }
    }

    // Ground truth for the editor's port of UnityEngine.Random: three Random.value in a row
    // after InitState, for a few seeds. Random.state is stripped from this build, so the
    // global state can't be put back; harmless here (runs once, in the menu, and the game
    // re-seeds with InitState before each spawn's roll).
    private static void LogUnityRandomSamples()
    {
        try
        {
            foreach (int seed in new[] { 0, 1, 2, 12345, 915142, -5, 2147483646 })
            {
                UnityEngine.Random.InitState(seed);
                float a = UnityEngine.Random.value, b = UnityEngine.Random.value, c = UnityEngine.Random.value;
                Plugin.Logger.LogInfo($"[random] unity seed {seed} values {a:R} {b:R} {c:R}");
            }
        }
        catch (Exception e) { Plugin.Logger.LogError($"[random] sampling UnityEngine.Random failed: {e.Message}"); }
    }

    // MyMath.roomSeed without initialising MyMath from managed code; null if unreadable.
    private static int? RoomSeed()
    {
        if (_myMath == IntPtr.Zero)
            _myMath = IL2CPP.GetIl2CppClass("Assembly-CSharp.dll", "", "MyMath");
        if (_myMath == IntPtr.Zero)
            return null;
        IntPtr statics = Marshal.ReadIntPtr(_myMath, 0xB8);
        return statics == IntPtr.Zero ? null : Marshal.ReadInt32(statics, 0x18);
    }

    // MyMath.DEBUG_useMathRandom (static +0x1C): when set, randomFromMetaSpawn skips the
    // seeded generator and returns MathFlash.random().
    private static bool? DebugUseMathRandom()
    {
        if (RoomSeed() == null)
            return null;
        return Marshal.ReadByte(Marshal.ReadIntPtr(_myMath, 0xB8), 0x1C) != 0;
    }

    private static void Log(string s)
    {
        _lines++;
        Plugin.Logger.LogInfo("[random] " + s);
    }

    // Params can hold random() expressions ("random(5)+3"); log what they came out as, to
    // tell which random roll the expression parser used.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(RoundSpikes), nameof(RoundSpikes.applyParams))]
    private static void ApplyParamsAfter(RoundSpikes __instance)
    {
        if (!Plugin.TraceRandom.Value || _lines >= MaxLines)
            return;
        try
        {
            var ms = __instance.metaSpawn;
            Log($"RoundSpikes.applyParams idInSong {ms?.idInSong} randomOffset {ms?.randomOffset} roomSeed {RoomSeed()}: " +
                $"speed {__instance.speed:R} size {__instance.size:R} at ({__instance.px:R}, {__instance.py:R})");
        }
        catch (Exception e) { Log("applyParams trace failed: " + e.Message); }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(RoundSpikes), nameof(RoundSpikes.setRandomPos))]
    private static void SetRandomPosBefore(RoundSpikes __instance, out (float, float) __state)
    {
        __state = (__instance.px, __instance.py);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(RoundSpikes), nameof(RoundSpikes.setRandomPos))]
    private static void SetRandomPosAfter(RoundSpikes __instance, (float, float) __state)
    {
        if (!Plugin.TraceRandom.Value)
            return;
        try
        {
            int seed = RoomSeed() ?? int.MinValue;
            if (seed != _lastSeed)
            {
                _lastSeed = seed;
                _lines = 0;
                Log($"roomSeed {seed} DEBUG_useMathRandom {DebugUseMathRandom()}");
            }
            if (_lines >= MaxLines)
                return;
            var ms = __instance.metaSpawn;
            var pos = ms?.metaParams?.getPosition();
            Log($"RoundSpikes.setRandomPos idInSong {ms?.idInSong} randomOffset {ms?.randomOffset} " +
                $"posUsed {pos?.xUsed}/{pos?.yUsed}: ({__state.Item1:R}, {__state.Item2:R}) -> ({__instance.px:R}, {__instance.py:R})");
        }
        catch (Exception e) { Log("setRandomPos trace failed: " + e.Message); }
    }
}
