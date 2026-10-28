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
    public const string Version = "0.1.0";

    internal static ManualLogSource Logger;

    internal static ConfigEntry<bool> NoHitEnabled;
    internal static ConfigEntry<float> RestartDelay;
    internal static ConfigEntry<KeyCode> NoHitToggleKey;
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
