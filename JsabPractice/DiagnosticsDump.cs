using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppInterop.Common;
using UnityEngine;

namespace JsabPractice;

// Development aid: native addresses for static analysis, plus sampled collision
// coordinates for calibrating the hitbox overlay against the screen.
internal static class DiagnosticsDump
{
    private static readonly (Type type, string method)[] Targets =
    {
        (typeof(RegisterManager), nameof(RegisterManager.RegisterHit)),
        (typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.hitByEnemy)),
        (typeof(HeroCollisionWithEnemy), nameof(HeroCollisionWithEnemy.die)),
        (typeof(HeroCooldown), nameof(HeroCooldown.isInvincible)),
        (typeof(ActorNormalLevelLogic), nameof(ActorNormalLevelLogic.getNormalLevelLogic)),
        (typeof(ActorArcadeLevelLogic), nameof(ActorArcadeLevelLogic.restartSong)),
        (typeof(UI_PauseNew), nameof(UI_PauseNew.restart)),
        (typeof(LogicRewindMusic), nameof(LogicRewindMusic.afterRewindToRestart)),
        (typeof(CollisionCircleComponent), nameof(CollisionCircleComponent.attackTargets)),
        (typeof(CollisionCircleComponent), nameof(CollisionCircleComponent.drawCircle)),
        (typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.attackTargets)),
        (typeof(CollisionRectangleComponent), nameof(CollisionRectangleComponent.drawRectangle)),
        (typeof(LogicDoCollisionWithRenderChild), nameof(LogicDoCollisionWithRenderChild.doCollisionWithChild)),
        (typeof(DebugShortcut), nameof(DebugShortcut.setMusicSpeed)),
    };

    private const float SampleInterval = 1f;
    private static float _nextSample;
    private static bool _wantCircle, _wantRect;

    public static void LogMethodAddresses()
    {
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .FirstOrDefault(m => string.Equals(m.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
        if (module == null)
        {
            Plugin.Logger.LogWarning("[diag] GameAssembly.dll not found in process modules");
            return;
        }

        long moduleBase = module.BaseAddress.ToInt64();
        Plugin.Logger.LogInfo($"[diag] GameAssembly.dll base=0x{moduleBase:X}");

        foreach (var (type, name) in Targets)
        {
            foreach (var method in AccessTools.GetDeclaredMethods(type).Where(m => m.Name == name))
            {
                try
                {
                    var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
                    var methodInfo = (IntPtr)field.GetValue(null);
                    // Il2CppMethodInfo begins with its methodPointer.
                    long code = Marshal.ReadIntPtr(methodInfo).ToInt64();
                    var args = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                    Plugin.Logger.LogInfo($"[diag] RVA 0x{code - moduleBase:X8}  {type.Name}.{name}({args})");
                }
                catch (Exception e)
                {
                    Plugin.Logger.LogWarning($"[diag] {type.Name}.{name}: {e.Message}");
                }
            }
        }
    }

    public static void Update()
    {
        if (Time.unscaledTime < _nextSample)
            return;
        _nextSample = Time.unscaledTime + SampleInterval;
        _wantCircle = _wantRect = true;

        try
        {
            var heroes = Hero.allCollisionEnemy;
            if (heroes == null || heroes.Length == 0)
                return;

            var stage = MainGame.stage;
            string stageInfo = stage == null ? "stage=null"
                : $"stage xy=({stage.x:0.0},{stage.y:0.0}) scale=({stage.scaleX:0.000},{stage.scaleY:0.000}) size=({stage.stageWidth:0},{stage.stageHeight:0})";
            var cam = stage?.gettk2dCamera();
            string camInfo = cam == null ? "cam=null"
                : $"cam pos={cam.transform.position} ortho={cam.orthographic} size={cam.orthographicSize:0.00} pixelRect={cam.pixelRect}";
            Plugin.Logger.LogInfo($"[diag] screen={Screen.width}x{Screen.height} {stageInfo} {camInfo}");

            for (int i = 0; i < heroes.Length; i++)
            {
                var c = heroes[i]?.circleCol;
                var a = heroes[i]?.actor;
                if (c == null || a == null) continue;
                string rel = "n/a";
                try { if (a.renderComponent != null) rel = a.renderComponent.getXRelativeToScreen().ToString("0.0"); }
                catch (Exception e) { rel = e.GetType().Name; }
                Plugin.Logger.LogInfo($"[diag] hero[{i}] pos x={a.px:0.0} y={a.py:0.0} xRelToScreen={rel} r={c.radius:0.0}");
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[diag] sample failed: {e.Message}");
        }
    }

    public static void SampleCircle(float x, float y, float radius)
    {
        if (!_wantCircle) return;
        _wantCircle = false;
        Plugin.Logger.LogInfo($"[diag] hazard circle x={x:0.0} y={y:0.0} r={radius:0.0}");
    }

    public static void SampleRect(float x, float y, float scaleX, float scaleY, float rot)
    {
        if (!_wantRect) return;
        _wantRect = false;
        Plugin.Logger.LogInfo($"[diag] hazard rect x={x:0.0} y={y:0.0} sx={scaleX:0.0} sy={scaleY:0.0} rot={rot:0.0}");
    }

    public static void SampleRectLines(RectangleLines r)
    {
        if (!_wantRect) return;
        _wantRect = false;
        Plugin.Logger.LogInfo($"[diag] hazard rectLines tl={r.topLeft} tr={r.topRight} br={r.bottomRight} bl={r.bottomLeft}");
    }
}
