using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace JsabPractice;

// Development aid for the level editor: for the hazard types listed in config, log what
// each spawn was configured with and where its actor and hitbox actually end up, so the
// meaning of params like modelSide/pos can be measured instead of guessed.
[HarmonyPatch]
internal static class SpawnTrace
{
    private const float Duration = 4f;
    private const float Interval = 0.25f;

    private class Entry
    {
        public Spawn Spawn;
        public IntPtr Pointer;
        public string Label;
        public float Start;
        public float NextLog;
        public string Shape;
    }

    private static readonly List<Entry> Active = new();
    private static int _counter;
    private static string _typesSource;
    private static HashSet<string> _types = new();

    private static HashSet<string> Types
    {
        get
        {
            string src = Plugin.TraceSpawnTypes.Value ?? "";
            if (src != _typesSource)
            {
                _typesSource = src;
                _types = new HashSet<string>(src.Split(',').Select(t => t.Trim()).Where(t => t.Length > 0), StringComparer.OrdinalIgnoreCase);
            }
            return _types;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MetaSpawn), nameof(MetaSpawn.createSpawn))]
    private static void CreateSpawn(MetaSpawn __instance, Spawn __result)
    {
        if (__result == null || Types.Count == 0)
            return;

        try
        {
            string id = __instance.modelSpawn?.id;
            if (id == null || !Types.Contains(id))
                return;

            var group = __instance.metaParams;
            var pos = group?.metaPosition;
            var timeline = group?.metaTimeline;
            string side = group != null && group.hasParamStr("modelSide") ? group.getStringStr("modelSide") : "-";
            string label = $"{id}#{++_counter}";

            Plugin.Logger.LogInfo($"[trace] {label} created class={__result.GetIl2CppType().Name} timeMs={timeline?.timeInMs?.value} " +
                                  $"side={side} pos=({pos?.xRelativeToScreen?.value},{pos?.yRelativeToScreen?.value}) used=({pos?.xUsed},{pos?.yUsed}) " +
                                  $"name='{timeline?.name}' actor=({__result.px:0},{__result.py:0})");

            float now = Time.unscaledTime;
            Active.Add(new Entry { Spawn = __result, Pointer = __result.Pointer, Label = label, Start = now, NextLog = now });
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[trace] createSpawn hook failed: {e.Message}");
        }
    }

    public static bool Tracing => Active.Count > 0;

    public static void OnShape(Actor attacker, string shape)
    {
        if (attacker == null)
            return;
        IntPtr ptr = attacker.Pointer;
        foreach (var e in Active)
        {
            if (e.Pointer == ptr)
            {
                e.Shape = shape;
                return;
            }
        }
    }

    public static void Update()
    {
        if (Active.Count == 0)
            return;

        float now = Time.unscaledTime;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            var e = Active[i];
            float age = now - e.Start;
            bool gone;
            try { gone = e.Spawn == null || e.Spawn.destroyed; }
            catch { gone = true; }

            if (gone || age > Duration)
            {
                Plugin.Logger.LogInfo($"[trace] {e.Label} {(gone ? "destroyed" : "trace ended")} at +{age:0.00}s");
                Active.RemoveAt(i);
                continue;
            }

            if (now < e.NextLog)
                continue;
            e.NextLog = now + Interval;
            Plugin.Logger.LogInfo($"[trace] {e.Label} +{age:0.00}s actor=({e.Spawn.px:0},{e.Spawn.py:0}) hitbox={e.Shape ?? "none"}");
            e.Shape = null;
        }
    }
}
