using System;
using UnityEngine;

namespace JsabPractice;

public class PracticeRunner : MonoBehaviour
{
    private bool _guiBroken;
    private GUIStyle _bannerStyle;

    public PracticeRunner(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        if (Input.GetKeyDown(Plugin.NoHitToggleKey.Value))
        {
            Plugin.NoHitEnabled.Value = !Plugin.NoHitEnabled.Value;
            Plugin.Logger.LogInfo($"No-hit mode {(Plugin.NoHitEnabled.Value ? "ON" : "OFF")}");
        }

        if (Input.GetKeyDown(Plugin.HitboxToggleKey.Value))
        {
            Plugin.HitboxesEnabled.Value = !Plugin.HitboxesEnabled.Value;
            if (!Plugin.HitboxesEnabled.Value)
                Hitboxes.Clear();
            Plugin.Logger.LogInfo($"Hitboxes {(Plugin.HitboxesEnabled.Value ? "ON" : "OFF")}");
        }

        if (Input.GetKeyDown(Plugin.FreezeToggleKey.Value))
        {
            Plugin.FreezeOnHit.Value = !Plugin.FreezeOnHit.Value;
            Plugin.Logger.LogInfo($"Freeze-on-hit {(Plugin.FreezeOnHit.Value ? "ON" : "OFF")}");
        }

        NoHit.Update();
        SpawnTrace.Update();

        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.Update();
    }

    private void LateUpdate()
    {
        NoHit.LateUpdate();
        Hitboxes.Prune();
    }

    private void OnGUI()
    {
        if (Event.current.type == EventType.Repaint)
            Hitboxes.Draw();

        if (_guiBroken)
            return;

        try
        {
            if (Plugin.ShowStatusLabel.Value)
            {
                GUI.Label(new Rect(10, 10, 600, 24),
                    $"No-hit: {OnOff(Plugin.NoHitEnabled.Value)} [{Plugin.NoHitToggleKey.Value}]   " +
                    $"Hitboxes: {OnOff(Plugin.HitboxesEnabled.Value)} [{Plugin.HitboxToggleKey.Value}]   " +
                    $"Freeze: {OnOff(Plugin.FreezeOnHit.Value)} [{Plugin.FreezeToggleKey.Value}]");
            }

            if (NoHit.IsFrozen)
            {
                _bannerStyle ??= new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(18, Screen.height / 30),
                    alignment = TextAnchor.MiddleCenter,
                };
                GUI.Label(new Rect(0, Screen.height * 0.08f, Screen.width, Screen.height / 15f),
                    "HIT - press Space / Enter to restart", _bannerStyle);
            }
        }
        catch (Exception e)
        {
            _guiBroken = true;
            Plugin.Logger.LogWarning($"Status label disabled, IMGUI unavailable: {e.Message}");
        }
    }

    private static string OnOff(bool b) => b ? "ON" : "OFF";
}
