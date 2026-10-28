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

        NoHit.Update();

        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.Update();
    }

    private void OnGUI()
    {
        if (_guiBroken || !Plugin.ShowStatusLabel.Value)
            return;

        try
        {
            GUI.Label(new Rect(10, 10, 300, 24), $"No-hit: {(Plugin.NoHitEnabled.Value ? "ON" : "OFF")} [{Plugin.NoHitToggleKey.Value}]");
        }
        catch (Exception e)
        {
            _guiBroken = true;
            Plugin.Logger.LogWarning($"Status label disabled, IMGUI unavailable: {e.Message}");
        }
    }
}
