using System;
using Rewired;

namespace JsabPractice;

// The game reads the mouse and keyboard through Rewired, which sees them even while the
// window is in the background: a click in another window can open a menu and leave the
// level. Unattended runs (auto-record) turn the game's mouse and keyboard off. The mod's
// own hotkeys use UnityEngine.Input and keep working.
internal static class GameInput
{
    private static bool _blocked;

    public static void SetBlocked(bool blocked)
    {
        if (blocked == _blocked || !ReInput.isReady) return;
        try
        {
            ReInput.controllers.Mouse.enabled = !blocked;
            ReInput.controllers.Keyboard.enabled = !blocked;
            _blocked = blocked;
            Plugin.Logger.LogInfo($"[input] game mouse and keyboard {(blocked ? "off" : "on")}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[input] couldn't {(blocked ? "block" : "unblock")} game input: {e.Message}");
            _blocked = blocked; // don't retry every frame
        }
    }
}
