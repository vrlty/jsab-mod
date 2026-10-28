using System;
using UnityEngine;

namespace JsabPractice;

public class PracticeRunner : MonoBehaviour
{
    private bool _guiBroken;

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

        NoHit.Update();

        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.Update();
    }

    private void LateUpdate()
    {
        Hitboxes.Prune();
    }

    private void OnGUI()
    {
        if (Event.current.type == EventType.Repaint)
            Hitboxes.Draw();

        if (_guiBroken || !Plugin.ShowStatusLabel.Value)
            return;

        try
        {
            GUI.Label(new Rect(10, 10, 400, 24),
                $"No-hit: {(Plugin.NoHitEnabled.Value ? "ON" : "OFF")} [{Plugin.NoHitToggleKey.Value}]   " +
                $"Hitboxes: {(Plugin.HitboxesEnabled.Value ? "ON" : "OFF")} [{Plugin.HitboxToggleKey.Value}]");
        }
        catch (Exception e)
        {
            _guiBroken = true;
            Plugin.Logger.LogWarning($"Status label disabled, IMGUI unavailable: {e.Message}");
        }
    }
}
