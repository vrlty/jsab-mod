using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace JsabPractice;

// Records every hazard shape the game tests against the players and draws them,
// plus the players' hurtboxes, as GL lines over the frame. Can also hold a frozen
// snapshot taken at the moment of a hit.
internal static class Hitboxes
{
    private const int CircleSegments = 24;
    // Hazards don't all hit-test every frame; keep shapes alive briefly to avoid flicker.
    private const int KeepFrames = 3;

    private struct Quad
    {
        public Vector2 A, B, C, D;
        public int Frame;
    }

    private struct Ring
    {
        public float X, Y, R;
        public int Frame;
    }

    private static readonly List<Ring> Circles = new();
    private static readonly List<Quad> Quads = new();

    private static readonly List<Ring> FrozenCircles = new();
    private static readonly List<Quad> FrozenQuads = new();
    private static readonly List<Ring> FrozenHeroes = new();
    private static readonly List<Ring> LiveHeroes = new();
    private static bool _frozen;

    private static Material _material;
    private static bool _broken;

    // The camera the game renders the stage through, fetched once per Draw.
    private static Camera _camera;
    private static float _ySign;

    private static readonly Color HazardColor = new(1f, 0.15f, 0.35f, 1f);
    private static readonly Color PlayerColor = new(0.2f, 1f, 1f, 1f);

    // Shapes are needed for the live overlay and for the freeze-on-hit snapshot.
    private static bool Recording =>
        Plugin.HitboxesEnabled.Value || (Plugin.NoHitEnabled.Value && Plugin.FreezeOnHit.Value);

    public static void AddCircle(float x, float y, float r)
    {
        if (!Recording) return;
        Circles.Add(new Ring { X = x, Y = y, R = r, Frame = Time.frameCount });
    }

    // Unverified convention: scale is the full width/height and rot is in degrees.
    public static void AddRect(float x, float y, float scaleX, float scaleY, float rot)
    {
        if (!Recording) return;
        float rad = rot * Mathf.Deg2Rad, cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
        float hx = scaleX * 0.5f, hy = scaleY * 0.5f;
        Vector2 Corner(float cx, float cy) => new(x + cx * cos - cy * sin, y + cx * sin + cy * cos);
        Quads.Add(new Quad { A = Corner(-hx, -hy), B = Corner(hx, -hy), C = Corner(hx, hy), D = Corner(-hx, hy), Frame = Time.frameCount });
    }

    public static void AddQuad(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        if (!Recording) return;
        Quads.Add(new Quad { A = a, B = b, C = c, D = d, Frame = Time.frameCount });
    }

    public static void Prune()
    {
        int oldest = Time.frameCount - KeepFrames;
        Circles.RemoveAll(c => c.Frame < oldest);
        Quads.RemoveAll(q => q.Frame < oldest);
    }

    public static void Clear()
    {
        Circles.Clear();
        Quads.Clear();
    }

    public static void Freeze()
    {
        FrozenCircles.Clear();
        FrozenCircles.AddRange(Circles);
        FrozenQuads.Clear();
        FrozenQuads.AddRange(Quads);
        CollectHeroes(FrozenHeroes);
        _frozen = true;
    }

    public static void Unfreeze()
    {
        _frozen = false;
        FrozenCircles.Clear();
        FrozenQuads.Clear();
        FrozenHeroes.Clear();
    }

    private static void CollectHeroes(List<Ring> into)
    {
        into.Clear();
        var heroes = Hero.allCollisionEnemy;
        if (heroes == null)
            return;

        for (int i = 0; i < heroes.Length; i++)
        {
            var comp = heroes[i];
            var circle = comp?.circleCol;
            var actor = comp?.actor;
            if (circle == null || actor == null) continue;
            into.Add(new Ring { X = actor.px + circle.x, Y = actor.py + circle.y, R = circle.radius });
        }
    }

    // Called from OnGUI during the Repaint event.
    public static void Draw()
    {
        if (_broken || (!_frozen && !Plugin.HitboxesEnabled.Value))
            return;

        try
        {
            var mat = GetMaterial();
            if (mat == null)
                return;

            _camera = MainGame.stage?.gettk2dCamera();
            if (_camera == null)
                return;
            _ySign = Plugin.InvertY.Value ? -1f : 1f;

            List<Ring> circles = Circles, heroes = LiveHeroes;
            List<Quad> quads = Quads;
            if (_frozen)
            {
                circles = FrozenCircles;
                quads = FrozenQuads;
                heroes = FrozenHeroes;
            }
            else
            {
                CollectHeroes(LiveHeroes);
            }

            mat.SetPass(0);
            GL.PushMatrix();
            GL.LoadPixelMatrix();

            if (_frozen && Plugin.FreezeDimAmount.Value > 0f)
                DrawDim(Plugin.FreezeDimAmount.Value);

            GL.Begin(GL.LINES);
            GL.Color(HazardColor);
            foreach (var c in circles)
                DrawCircle(c.X, c.Y, c.R);
            foreach (var q in quads)
            {
                Line(q.A, q.B);
                Line(q.B, q.C);
                Line(q.C, q.D);
                Line(q.D, q.A);
            }

            GL.Color(PlayerColor);
            foreach (var h in heroes)
                DrawCircle(h.X, h.Y, h.R);
            GL.End();

            GL.PopMatrix();
        }
        catch (Exception e)
        {
            _broken = true;
            Plugin.Logger.LogError($"Hitbox overlay disabled after error: {e}");
        }
    }

    private static void DrawDim(float alpha)
    {
        GL.Begin(GL.QUADS);
        GL.Color(new Color(0f, 0f, 0f, Mathf.Clamp01(alpha)));
        GL.Vertex3(0f, 0f, 0f);
        GL.Vertex3(0f, Screen.height, 0f);
        GL.Vertex3(Screen.width, Screen.height, 0f);
        GL.Vertex3(Screen.width, 0f, 0f);
        GL.End();
    }

    private static void DrawCircle(float x, float y, float r)
    {
        Vector2 prev = new(x + r, y);
        for (int i = 1; i <= CircleSegments; i++)
        {
            float a = i * (Mathf.PI * 2f / CircleSegments);
            Vector2 next = new(x + Mathf.Cos(a) * r, y + Mathf.Sin(a) * r);
            Line(prev, next);
            prev = next;
        }
    }

    private static void Line(Vector2 a, Vector2 b)
    {
        GL.Vertex(ToScreen(a));
        GL.Vertex(ToScreen(b));
    }

    // Stage units are the tk2d camera's world units (Y-up), so project through it.
    // WorldToScreenPoint returns bottom-left-origin pixels, matching GL.LoadPixelMatrix.
    // (Stage.STAGE_WIDTH/HEIGHT is a stale 720x480 constant and doesn't describe the view.)
    private static Vector3 ToScreen(Vector2 p)
    {
        var s = _camera.WorldToScreenPoint(new Vector3(p.x, p.y * _ySign, 0f));
        s.z = 0f;
        return s;
    }

    private static Material GetMaterial()
    {
        if (_material != null)
            return _material;

        Shader shader = null;
        foreach (var name in new[] { "Hidden/Internal-Colored", "Sprites/Default", "UI/Default" })
        {
            shader = Shader.Find(name);
            if (shader != null)
            {
                Plugin.Logger.LogInfo($"Hitbox overlay using shader {name}");
                break;
            }
        }

        if (shader == null)
        {
            _broken = true;
            Plugin.Logger.LogError("Hitbox overlay: no usable shader found");
            return null;
        }

        _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        // Alpha blending for the dim layer; no depth test so lines always draw on top.
        if (_material.HasProperty("_SrcBlend"))
            _material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
        if (_material.HasProperty("_DstBlend"))
            _material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
        if (_material.HasProperty("_ZWrite"))
            _material.SetInt("_ZWrite", 0);
        if (_material.HasProperty("_ZTest"))
            _material.SetInt("_ZTest", (int)CompareFunction.Always);
        if (_material.HasProperty("_Cull"))
            _material.SetInt("_Cull", (int)CullMode.Off);
        return _material;
    }
}
