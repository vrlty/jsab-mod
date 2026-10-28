using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace JsabPractice;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "local.jsabpractice";
    public const string Name = "JSAB Practice";
    public const string Version = "0.7.0";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> NoHitEnabled;
    internal static ConfigEntry<float> RestartDelay;
    internal static ConfigEntry<KeyCode> NoHitToggleKey;
    internal static ConfigEntry<bool> FreezeOnHit;
    internal static ConfigEntry<KeyCode> FreezeToggleKey;
    internal static ConfigEntry<float> FreezeDimAmount;
    internal static ConfigEntry<bool> HitboxesEnabled;
    internal static ConfigEntry<KeyCode> HitboxToggleKey;
    internal static ConfigEntry<bool> InvertY;
    internal static ConfigEntry<bool> DumpLevels;
    internal static ConfigEntry<bool> CustomLevels;
    internal static ConfigEntry<KeyCode> ReloadLevelKey;
    internal static ConfigEntry<bool> ShowStatusLabel;
    internal static ConfigEntry<bool> Diagnostics;
    internal static ConfigEntry<string> TraceSpawnTypes;

    public override void Load()
    {
        Logger = Log;

        NoHitEnabled = Config.Bind("NoHit", "Enabled", true,
            "Restart the song as soon as you take a hit (S-rank practice).");
        RestartDelay = Config.Bind("NoHit", "RestartDelay", 0.35f,
            "Seconds to wait after the hit before restarting, so you can see what got you.");
        NoHitToggleKey = Config.Bind("NoHit", "ToggleKey", KeyCode.F1,
            "Key that toggles no-hit mode in game.");
        FreezeOnHit = Config.Bind("NoHit", "FreezeOnHit", false,
            "Instead of restarting right away, freeze on the hit and show the hitboxes; Space/Enter restarts.");
        FreezeToggleKey = Config.Bind("NoHit", "FreezeToggleKey", KeyCode.F3,
            "Key that toggles freeze-on-hit in game.");
        FreezeDimAmount = Config.Bind("NoHit", "FreezeDimAmount", 0.65f,
            "How much to darken the game while frozen (0 = none, 1 = black), so the hitboxes stand out.");
        HitboxesEnabled = Config.Bind("Hitboxes", "Enabled", true,
            "Draw hazard hitboxes (red) and your hurtbox (cyan).");
        HitboxToggleKey = Config.Bind("Hitboxes", "ToggleKey", KeyCode.F2,
            "Key that toggles the hitbox overlay in game.");
        InvertY = Config.Bind("Hitboxes", "InvertY", false,
            "Flip the overlay vertically (game Y points up, so this should normally stay off).");
        DumpLevels = Config.Bind("Levels", "DumpLevels", true,
            "Write each level the game loads to BepInEx/jsab-levels/vanilla as JSON.");
        CustomLevels = Config.Bind("Levels", "CustomLevels", true,
            "Replace a level with BepInEx/jsab-levels/custom/<LEVEL>.json when one exists (offline only).");
        ReloadLevelKey = Config.Bind("Levels", "ReloadKey", KeyCode.F5,
            "Re-read the custom level file for the song being played and restart it.");
        ShowStatusLabel = Config.Bind("UI", "ShowStatusLabel", true,
            "Draw a small status label in the corner of the screen.");
        Diagnostics = Config.Bind("Debug", "Diagnostics", true,
            "Log native method addresses and sampled collision shapes (used while building the mod).");
        TraceSpawnTypes = Config.Bind("Debug", "TraceSpawnTypes", "Wall",
            "Comma-separated hazard types (e.g. Wall,Beam) whose spawns are traced in the log: their settings, actor position and hitbox over time.");

        var harmony = new Harmony(Guid);
        harmony.PatchAll(typeof(Patches));
        harmony.PatchAll(typeof(LevelDump));
        harmony.PatchAll(typeof(LevelImport));
        harmony.PatchAll(typeof(SpawnTrace));
        AddComponent<PracticeRunner>();

        if (Diagnostics.Value)
            DiagnosticsDump.LogMethodAddresses();

        Logger.LogInfo($"{Name} {Version} loaded. No-hit: {(NoHitEnabled.Value ? "ON" : "OFF")} (toggle {NoHitToggleKey.Value}).");
    }
}
