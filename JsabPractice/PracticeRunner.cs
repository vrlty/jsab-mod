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
        if (Time.frameCount > 120)
            RandomTrace.Install();

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

        if (Input.GetKeyDown(Plugin.ReloadLevelKey.Value))
        {
            Plugin.Logger.LogInfo("Reloading custom level");
            NoHit.ReloadCustomLevel();
        }

        if (Plugin.RunInBackground.Value && !Application.runInBackground)
            Application.runInBackground = true;

        EditorBridge.Pump();
        AutoRecorder.Update();
        GameInput.SetBlocked(AutoRecorder.Active);
        NoHit.Update();
        SpawnTrace.Update();
        Recorder.NoteScenery();
        SpawnClassMap.Update();

        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.Update();
    }

    private void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus)
            return;
        if (Plugin.RunInBackground.Value)
            if (!AutoRecorder.Active) NoHit.OnFocusLost();   // an unattended run keeps going
        // Switching to the editor: make the latest motion available to it.
        Recorder.Save(background: true);
    }

    private void OnApplicationQuit() => Recorder.End();

    private void LateUpdate()
    {
        try { Recorder.Tick(); }
        catch (Exception e) { Plugin.Logger.LogWarning($"Recorder tick failed: {e.Message}"); }
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

            string banner = NoHit.IsFrozen ? "HIT - press Space / Enter to restart"
                : NoHit.IsPreviewPaused ? $"Preview from {FormatTime(NoHit.PreviewMs)} - press Space / Enter to play"
                : null;
            if (banner != null)
            {
                _bannerStyle ??= new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(18, Screen.height / 30),
                    alignment = TextAnchor.MiddleCenter,
                };
                GUI.Label(new Rect(0, Screen.height * 0.08f, Screen.width, Screen.height / 15f), banner, _bannerStyle);
            }
        }
        catch (Exception e)
        {
            _guiBroken = true;
            Plugin.Logger.LogWarning($"Status label disabled, IMGUI unavailable: {e.Message}");
        }
    }

    private static string OnOff(bool b) => b ? "ON" : "OFF";

    private static string FormatTime(float ms) => $"{(int)(ms / 60000)}:{ms % 60000 / 1000f:00.0}";
}
