using System;
using BepInEx.Configuration;
using UnityEngine;

namespace JsabPractice;

// The in-game menu (MenuKey, F1 by default): no-hit, freeze, hitboxes, practice checkpoints
// and key bindings in one place. It pauses the level while open, and the game's own mouse
// and keyboard are off meanwhile (GameInput), so clicks here don't reach it.
internal static class PracticeMenu
{
    public static bool IsOpen { get; private set; }
    public static bool IsRebinding => _rebinding != null;

    private static bool _pausedGame, _cursorVisible, _practiceOnOpen, _goToStart;
    private static CursorLockMode _cursorLock;
    private static int _startOnOpen;
    private static ConfigEntry<KeyCode> _rebinding;
    private static GUIStyle _title, _hint;

    private const float Width = 470f, RowH = 26f, Pad = 14f;

    private static (string Label, ConfigEntry<KeyCode> Key)[] Bindings => new[]
    {
        ("Open this menu", Plugin.MenuKey),
        ("No-hit mode", Plugin.NoHitToggleKey),
        ("Hitboxes", Plugin.HitboxToggleKey),
        ("Freeze on hit", Plugin.FreezeToggleKey),
        ("Reload custom level", Plugin.ReloadLevelKey),
    };

    // Settings shared by the menu and the hotkeys.
    internal static void SetNoHit(bool on)
    {
        Plugin.NoHitEnabled.Value = on;
        Plugin.Logger.LogInfo($"No-hit mode {(on ? "ON" : "OFF")}");
    }

    internal static void SetHitboxes(bool on)
    {
        Plugin.HitboxesEnabled.Value = on;
        if (!on) Hitboxes.Clear();
        Plugin.Logger.LogInfo($"Hitboxes {(on ? "ON" : "OFF")}");
    }

    internal static void SetFreeze(bool on)
    {
        Plugin.FreezeOnHit.Value = on;
        Plugin.Logger.LogInfo($"Freeze-on-hit {(on ? "ON" : "OFF")}");
    }

    public static void Toggle()
    {
        if (IsOpen) Close();
        else Open();
    }

    private static void Open()
    {
        IsOpen = true;
        _rebinding = null;
        _goToStart = false;
        _startOnOpen = Practice.StartIndex;
        _practiceOnOpen = Plugin.PracticeEnabled.Value;
        _cursorVisible = Cursor.visible;
        _cursorLock = Cursor.lockState;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        // Leave the game alone when something else already holds it (pause menu, no-hit freeze).
        _pausedGame = false;
        var pause = NoHit.GetArcadeLevel(quiet: true)?.logicUIPause;
        if (pause != null && NoHit.PauseMenu == null && !NoHit.IsBusy)
        {
            try { pause.pauseGameWithoutMenu(); _pausedGame = true; }
            catch (Exception e) { Plugin.Logger.LogWarning($"[menu] couldn't pause: {e.Message}"); }
        }
    }

    private static void Close()
    {
        IsOpen = false;
        _rebinding = null;
        Cursor.visible = _cursorVisible;
        Cursor.lockState = _cursorLock;

        var arcade = NoHit.GetArcadeLevel(quiet: true);
        if (arcade == null)
            return;
        if (_pausedGame)
        {
            try { arcade.logicUIPause?.unpause(); }
            catch (Exception e) { Plugin.Logger.LogWarning($"[menu] couldn't unpause: {e.Message}"); }
        }

        // A new start checkpoint takes effect right away, like picking one in GD's practice.
        bool startChanged = Practice.StartIndex != _startOnOpen
                            || (!_practiceOnOpen && Plugin.PracticeEnabled.Value && Practice.StartIndex > 0);
        if (_pausedGame && Practice.Active && (_goToStart || startChanged))
            Practice.GoToStart(arcade);
        _pausedGame = false;
    }

    public static void Draw()
    {
        if (!IsOpen)
            return;

        _title ??= new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold };
        _hint ??= new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };

        if (_rebinding != null)
            CaptureKey();

        float scale = Mathf.Max(1f, Screen.height / 720f);
        var saved = GUI.matrix;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float screenW = Screen.width / scale, screenH = Screen.height / scale;

        float height = Pad * 2 + 34 + RowH * 4 + 10 + PracticeRows() * RowH + 12 + RowH + Bindings.Length * RowH + RowH;
        var panel = new Rect((screenW - Width) / 2f, Mathf.Max(10f, (screenH - height) / 2f), Width, height);
        GUI.Box(panel, "");
        GUI.Box(panel, ""); // twice: the default box is see-through
        float x = panel.x + Pad, w = Width - Pad * 2, y = panel.y + Pad;

        GUI.Label(new Rect(x, y, w, 30), "JSAB Practice", _title);
        y += 34;

        bool noHit = GUI.Toggle(new Rect(x, y, w, RowH), Plugin.NoHitEnabled.Value, "  No-hit mode: any hit restarts");
        if (noHit != Plugin.NoHitEnabled.Value) SetNoHit(noHit);
        y += RowH;

        GUI.enabled = noHit;
        bool freeze = GUI.Toggle(new Rect(x + 20, y, w - 20, RowH), Plugin.FreezeOnHit.Value, "  Freeze on the hit first (Space / Enter continues)");
        if (freeze != Plugin.FreezeOnHit.Value) SetFreeze(freeze);
        GUI.enabled = true;
        y += RowH;

        bool boxes = GUI.Toggle(new Rect(x, y, w, RowH), Plugin.HitboxesEnabled.Value, "  Hitboxes");
        if (boxes != Plugin.HitboxesEnabled.Value) SetHitboxes(boxes);
        y += RowH;

        bool practice = GUI.Toggle(new Rect(x, y, w, RowH), Plugin.PracticeEnabled.Value, "  Practice from a checkpoint");
        if (practice != Plugin.PracticeEnabled.Value)
        {
            Plugin.PracticeEnabled.Value = practice;
            Plugin.Logger.LogInfo($"Practice mode {(practice ? "ON" : "OFF")}");
        }
        y += RowH;
        y = DrawPractice(x + 20, y, w - 20) + 10;

        GUI.Label(new Rect(x, y, w, RowH), "Keys: click one, then press a key (Esc cancels, Backspace clears)", _hint);
        y += RowH;
        foreach (var (label, key) in Bindings)
        {
            GUI.Label(new Rect(x, y, w - 160, RowH), label);
            string name = _rebinding == key ? "press a key..." : key.Value == KeyCode.None ? "(none)" : key.Value.ToString();
            if (GUI.Button(new Rect(x + w - 150, y + 2, 150, RowH - 4), name))
                _rebinding = _rebinding == key ? null : key;
            y += RowH;
        }

        GUI.Label(new Rect(x, y + 4, w, RowH), $"{Plugin.MenuKey.Value} closes this menu", _hint);
        GUI.matrix = saved;
    }

    private static int PracticeRows() =>
        !Plugin.PracticeEnabled.Value ? 0
        : !Practice.InLevel || Practice.Points.Count < 2 ? 1
        : 3;

    private static float DrawPractice(float x, float y, float w)
    {
        if (!Plugin.PracticeEnabled.Value)
            return y;
        if (!Practice.InLevel)
        {
            GUI.Label(new Rect(x, y, w, RowH), "Start a level to pick its checkpoints.", _hint);
            return y + RowH;
        }
        if (Practice.Points.Count < 2)
        {
            GUI.Label(new Rect(x, y, w, RowH), "This level has no checkpoints: deaths restart it.", _hint);
            return y + RowH;
        }

        if (Stepper(x, y, w, "Start", Practice.Describe(Practice.StartIndex), out int step))
            Practice.CycleStart(step);
        y += RowH;
        if (Stepper(x, y, w, "End", Practice.EndIndex < 0 ? "End of level" : Practice.Describe(Practice.EndIndex) + ": loops back", out step))
            Practice.CycleEnd(step);
        y += RowH;

        GUI.enabled = _pausedGame;
        if (GUI.Button(new Rect(x, y + 2, 200, RowH - 4), "Go to the start now"))
        {
            _goToStart = true;
            Close();
        }
        GUI.enabled = true;
        return y + RowH;
    }

    private static bool Stepper(float x, float y, float w, string label, string value, out int step)
    {
        step = 0;
        GUI.Label(new Rect(x, y, 50, RowH), label);
        if (GUI.Button(new Rect(x + 50, y + 2, 28, RowH - 4), "<")) step = -1;
        GUI.Label(new Rect(x + 86, y, w - 128, RowH), value);
        if (GUI.Button(new Rect(x + w - 34, y + 2, 28, RowH - 4), ">")) step = 1;
        return step != 0;
    }

    private static void CaptureKey()
    {
        var e = Event.current;
        if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None)
            return;
        e.Use();

        var target = _rebinding;
        _rebinding = null;
        var key = e.keyCode;
        if (key == KeyCode.Escape)
            return;
        if (key is KeyCode.Backspace or KeyCode.Delete)
        {
            if (target != Plugin.MenuKey) target.Value = KeyCode.None; // the menu must stay reachable
            return;
        }
        if (target != Plugin.MenuKey && key == Plugin.MenuKey.Value)
            return;

        foreach (var (_, other) in Bindings)
            if (other != target && other.Value == key)
                other.Value = KeyCode.None;
        target.Value = key;
        Plugin.Logger.LogInfo($"[menu] {target.Definition.Key} bound to {key}");
    }
}
