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

    // Rect hitboxes arrive as (x, y, scaleX, scaleY, rot) where scale multiplies an unknown
    // base size. Use the game's own circle-vs-rect test as an oracle: slide a rect away from
    // a tiny circle until they stop colliding, which measures the rect's half-extent exactly.
    private static bool _rectProbed;

    public static void ProbeRectGeometry()
    {
        if (_rectProbed)
            return;
        var actor = MainGame.dummyActor;
        if (actor == null)
            return;
        _rectProbed = true;

        CollisionCircleComponent comp = null;
        try
        {
            comp = CollisionCircleComponent.addToActor(actor);
            comp.addCircleCollision(0f, 0f, 1);
            float px = actor.px, py = actor.py;

            bool Hits(float dx, float dy, float sx, float sy, float rot)
            {
                var info = comp.isCollidingWithRectangle(px + dx, py + dy, sx, sy, rot);
                return info != null && !info.isNull;
            }

            // Largest offset that still collides, minus the circle's radius.
            float HalfExtent(float sx, float sy, float rot, bool alongX)
            {
                if (!Hits(0, 0, sx, sy, rot)) return float.NaN;
                float lo = 0, hi = 10000;
                for (int i = 0; i < 40; i++)
                {
                    float m = (lo + hi) / 2;
                    if (Hits(alongX ? m : 0, alongX ? 0 : m, sx, sy, rot)) lo = m; else hi = m;
                }
                return lo - 1f;
            }

            foreach (var (sx, sy, rot) in new[] { (1f, 1f, 0f), (2f, 1f, 0f), (2f, 1f, 90f), (2f, 1f, Mathf.PI / 2), (25f, 3f, 90f) })
                Plugin.Logger.LogInfo($"[diag] rect scale=({sx},{sy}) rot={rot:0.###}: half-extent x={HalfExtent(sx, sy, rot, true):0.###} y={HalfExtent(sx, sy, rot, false):0.###}");

            // Direction of positive rotation: a long thin rect at +30 degrees covers the point
            // at +30 degrees if rotation is counter-clockwise (Y-up), the point at -30 if not.
            float d = 90f, c30 = Mathf.Cos(30f * Mathf.Deg2Rad) * d, s30 = Mathf.Sin(30f * Mathf.Deg2Rad) * d;
            Plugin.Logger.LogInfo($"[diag] rect rot=+30: hits +30deg point={Hits(c30, s30, 4f, 0.2f, 30f)}, hits -30deg point={Hits(c30, -s30, 4f, 0.2f, 30f)}");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[diag] rect probe failed: {e}");
        }
        finally
        {
            if (comp != null)
            {
                try { actor.removeComponent(comp); } catch { /* best effort */ }
            }
        }
    }

    // Which way is +y on screen? Project stage points through the game's own camera.
    private static bool _axesProbed;

    private static void ProbeAxes()
    {
        if (_axesProbed) return;
        var cam = MainGame.stage?.gettk2dCamera();
        if (cam == null) return;
        _axesProbed = true;
        Vector3 S(float x, float y) => cam.WorldToScreenPoint(new Vector3(x, y, 0f));
        Plugin.Logger.LogInfo($"[diag] screen (bottom-left origin) of stage (0,0)={S(0, 0)} (0,+100)={S(0, 100)} (0,-100)={S(0, -100)} (+100,0)={S(100, 0)}; " +
                              $"camera rotation={cam.transform.eulerAngles} scale={cam.transform.lossyScale}");
    }

    public static void Update()
    {
        ProbeRectGeometry();
        ProbeAxes();
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
