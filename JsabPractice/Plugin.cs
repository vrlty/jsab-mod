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
    public const string Version = "0.3.0";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> NoHitEnabled;
    internal static ConfigEntry<float> RestartDelay;
    internal static ConfigEntry<KeyCode> NoHitToggleKey;
    internal static ConfigEntry<bool> HitboxesEnabled;
    internal static ConfigEntry<KeyCode> HitboxToggleKey;
    internal static ConfigEntry<float> StageWidthOverride;
    internal static ConfigEntry<float> StageHeightOverride;
    internal static ConfigEntry<bool> InvertY;
    internal static ConfigEntry<bool> ShowStatusLabel;
    internal static ConfigEntry<bool> Diagnostics;

    public override void Load()
    {
        Logger = Log;

        NoHitEnabled = Config.Bind("NoHit", "Enabled", true,
            "Restart the song as soon as you take a hit (S-rank practice).");
        RestartDelay = Config.Bind("NoHit", "RestartDelay", 0.35f,
            "Seconds to wait after the hit before restarting, so you can see what got you.");
        NoHitToggleKey = Config.Bind("NoHit", "ToggleKey", KeyCode.F1,
            "Key that toggles no-hit mode in game.");
        HitboxesEnabled = Config.Bind("Hitboxes", "Enabled", true,
            "Draw hazard hitboxes (red) and your hurtbox (cyan).");
        HitboxToggleKey = Config.Bind("Hitboxes", "ToggleKey", KeyCode.F2,
            "Key that toggles the hitbox overlay in game.");
        StageWidthOverride = Config.Bind("Hitboxes", "StageWidthOverride", 0f,
            "Logical stage width used to map game coordinates to the screen. 0 = read it from the game.");
        StageHeightOverride = Config.Bind("Hitboxes", "StageHeightOverride", 0f,
            "Logical stage height. 0 = read it from the game.");
        InvertY = Config.Bind("Hitboxes", "InvertY", false,
            "Flip the overlay vertically (game Y points up, so this should normally stay off).");
        ShowStatusLabel = Config.Bind("UI", "ShowStatusLabel", true,
            "Draw a small status label in the corner of the screen.");
        Diagnostics = Config.Bind("Debug", "Diagnostics", true,
            "Log native method addresses and sampled collision shapes (used while building the mod).");

        Harmony.CreateAndPatchAll(typeof(Patches), Guid);
        AddComponent<PracticeRunner>();

        if (Diagnostics.Value)
            DiagnosticsDump.LogMethodAddresses();

        Logger.LogInfo($"{Name} {Version} loaded. No-hit: {(NoHitEnabled.Value ? "ON" : "OFF")} (toggle {NoHitToggleKey.Value}).");
    }
}
