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

        HandleHotkeys();

        if (Plugin.RunInBackground.Value && !Application.runInBackground)
            Application.runInBackground = true;

        EditorBridge.Pump();
        CustomSongs.Update();
        AutoRecorder.Update();
        // Rewired reads the keyboard even while the game is in the background (RunInBackground),
        // so typing in another window drove the game's menus (it changed the language, keyboard
        // layout and resolution on 2026-11-15). The game only takes input while focused.
        GameInput.SetBlocked(AutoRecorder.Active || !Application.isFocused || PracticeMenu.IsOpen);
        NoHit.Update();
        Practice.Update();
        SpawnTrace.Update();
        Recorder.NoteScenery();
        SpawnClassMap.Update();

        if (Plugin.Diagnostics.Value)
            DiagnosticsDump.Update();
    }

    // The menu key always works (except while the menu waits for a key to bind); the others
    // only while the menu is closed. A key that's also the menu key belongs to the menu.
    private static void HandleHotkeys()
    {
        KeyCode menu = Plugin.MenuKey.Value;
        if (!PracticeMenu.IsRebinding && menu != KeyCode.None && Input.GetKeyDown(menu))
        {
            PracticeMenu.Toggle();
            return;
        }
        if (PracticeMenu.IsOpen)
            return;

        if (Pressed(Plugin.NoHitToggleKey.Value, menu)) PracticeMenu.SetNoHit(!Plugin.NoHitEnabled.Value);
        if (Pressed(Plugin.HitboxToggleKey.Value, menu)) PracticeMenu.SetHitboxes(!Plugin.HitboxesEnabled.Value);
        if (Pressed(Plugin.FreezeToggleKey.Value, menu)) PracticeMenu.SetFreeze(!Plugin.FreezeOnHit.Value);
        if (Pressed(Plugin.ReloadLevelKey.Value, menu))
        {
            Plugin.Logger.LogInfo("Reloading custom level");
            NoHit.ReloadCustomLevel();
        }
    }

    private static bool Pressed(KeyCode key, KeyCode menu) =>
        key != KeyCode.None && key != menu && Input.GetKeyDown(key);

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
            if (Plugin.ShowStatusLabel.Value && !PracticeMenu.IsOpen)
                GUI.Label(new Rect(10, 10, 900, 24), StatusText());

            PracticeMenu.Draw();

            string banner = NoHit.IsFrozen ? "HIT - press Space / Enter to restart"
                : NoHit.IsPreviewPaused ? $"Preview from {FormatTime(NoHit.PreviewMs)} - press Space / Enter to play"
                : Practice.Banner;
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

    // "[F1] menu   No-hit · Hitboxes · Practice: Checkpoint 2 (1:33.9) to end of level"
    private static string StatusText()
    {
        var on = new System.Collections.Generic.List<string>();
        if (Plugin.NoHitEnabled.Value) on.Add(Plugin.FreezeOnHit.Value ? "No-hit (freeze)" : "No-hit");
        if (Plugin.HitboxesEnabled.Value) on.Add("Hitboxes");
        if (Plugin.PracticeEnabled.Value)
            on.Add(Practice.InLevel && Practice.Points.Count > 1
                ? $"Practice: {Practice.Describe(Practice.StartIndex)} to {Practice.Describe(Practice.EndIndex).ToLowerInvariant()}"
                : "Practice");
        return $"[{Plugin.MenuKey.Value}] menu   " + string.Join("  ·  ", on);
    }

    private static string FormatTime(float ms) => $"{(int)(ms / 60000)}:{ms % 60000 / 1000f:00.0}";
}
