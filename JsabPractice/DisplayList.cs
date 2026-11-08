using System;
using System.Collections.Generic;
using System.Text.Json;
using Assets.Plugins.Berzerk;

namespace JsabPractice;

// The game draws with its own Flash runtime: a display list of DisplayObjects (MovieClips
// built from ModelFlaAsset symbols) under CameraFlashContainers' layers. This reads it, for
// the motion recorder (what's on screen, at which frame, where) and for diagnostics:
//   GET /display?depth=N  -> every layer's objects as JSON
internal static class DisplayList
{
    // Layers that hold hazards, by the name the recorder and the dump use.
    public static IEnumerable<(string name, DisplayObjectContainer layer)> Layers()
    {
        var c = CameraFlashContainers.instance;
        if (c == null) yield break;
        yield return ("backFx", c.backFxVisual);
        yield return ("enemy", c.enemyVisual);
        yield return ("enemy2", c.enemy2Visual);
        yield return ("ammo", c.ammoVisual);
        yield return ("hero", c.heroVisual);
        yield return ("foreFx", c.foreFxVisual);
        yield return ("fore2Fx", c.fore2FxVisual);
    }

    public static IEnumerable<DisplayObject> Children(DisplayObjectContainer parent)
    {
        if (parent == null) yield break;
        int n;
        try { n = parent.numChildren; }
        catch { yield break; }
        for (int i = 0; i < n; i++)
        {
            DisplayObject child = null;
            try { child = parent.getChildAt(i); }
            catch { /* list changed under us */ }
            if (child != null) yield return child;
        }
    }

    // The asset bundle a symbol came from ("jsb_ingamevisuals"), as tools/extract_art.py names
    // its exports, or null.
    private static readonly Dictionary<IntPtr, string> BundleByAsset = new();
    public static string BundleOf(DisplayObject d)
    {
        try
        {
            var asset = d.metaDisplayObject?.modelFlaAsset;
            if (asset == null) return null;
            if (BundleByAsset.TryGetValue(asset.Pointer, out var known)) return known;
            string bundle = null;
            try
            {
                string path = ModelFlaEnum.getFromModelAsset(asset)?.getAssetBundlePath();
                if (!string.IsNullOrEmpty(path)) bundle = path.Substring(path.LastIndexOf('/') + 1).ToLowerInvariant();
            }
            catch { /* fall back to the document name */ }
            bundle ??= asset.docName?.ToLowerInvariant();
            BundleByAsset[asset.Pointer] = bundle;
            return bundle;
        }
        catch
        {
            return null;
        }
    }

    // Symbol space -> global (the Flash stage) as [a, b, c, d, tx, ty], measured with
    // localToGlobal so it includes every parent's transform.
    public static float[] GlobalMatrix(DisplayObject d)
    {
        var o = d.localToGlobal(new Point(0f, 0f));
        var x = d.localToGlobal(new Point(100f, 0f));
        var y = d.localToGlobal(new Point(0f, 100f));
        return new[] { (x.x - o.x) / 100f, (x.y - o.y) / 100f, (y.x - o.x) / 100f, (y.y - o.y) / 100f, o.x, o.y };
    }

    // The game's world matrix (Unity, y up) as a y-down affine [a, b, c, d, tx, ty]
    // (x' = a x + c y + tx, y' = b x + d y + ty).
    public static float[] WorldAffine(DisplayObject d)
    {
        var w = d.transform.concatenedMatrix;
        return new[] { w.m00, -w.m10, -w.m01, w.m11, w.m03, -w.m13 };
    }

    // d's matrix relative to its parent, from the world matrices the game draws with.
    public static float[] LocalMatrix(DisplayObject d)
    {
        try
        {
            var world = WorldAffine(d);
            var parent = d.parent;
            if (parent == null) return world;
            var pw = WorldAffine(parent);
            if (MathF.Abs(pw[0] * pw[3] - pw[1] * pw[2]) < 1e-9f) return null;   // parent scaled to nothing
            return Multiply(Invert(pw), world);
        }
        catch
        {
            return null;
        }
    }

    public static float[] Multiply(float[] p, float[] q) => new[]
    {
        p[0] * q[0] + p[2] * q[1], p[1] * q[0] + p[3] * q[1],
        p[0] * q[2] + p[2] * q[3], p[1] * q[2] + p[3] * q[3],
        p[0] * q[4] + p[2] * q[5] + p[4], p[1] * q[4] + p[3] * q[5] + p[5],
    };

    public static float[] Invert(float[] m)
    {
        float det = m[0] * m[3] - m[1] * m[2];
        float a = m[3] / det, b = -m[1] / det, c = -m[2] / det, d = m[0] / det;
        return new[] { a, b, c, d, -(a * m[4] + c * m[5]), -(b * m[4] + d * m[5]) };
    }

    public static float ConcatAlpha(DisplayObject d)
    {
        float a = 1f;
        for (var p = d; p != null; p = p.parent)
        {
            try { a *= p.alpha; }
            catch { break; }
        }
        return a;
    }

    public static string Dump(int depth)
    {
        var layers = new Dictionary<string, object>();
        foreach (var (name, layer) in Layers())
            layers[name] = layer == null ? null : Describe(layer, depth, 0);

        // A few live actors with the coordinates hitboxes are measured in, to line the two up.
        var actors = new List<object>();
        try
        {
            foreach (var d in Children(CameraFlashContainers.instance?.enemyVisual))
            {
                if (actors.Count >= 6) break;
                actors.Add(new { cls = d.className, global = Round(GlobalMatrix(d)) });
            }
        }
        catch (Exception e) { actors.Add(new { error = e.Message }); }

        var arcade = NoHit.GetArcadeLevel(quiet: true);
        EditorBridge.TryReadMusic(arcade?.logicEnemyCreator?.sfxView, out float timeMs, out _);
        return JsonSerializer.Serialize(new { ok = true, timeMs, layers, actors = HazardActors(), sample = actors });
    }

    // Actors' own positions (stage units; hitboxes use these) next to their view's: the
    // players, and hazards the recorder saw hit-testing recently.
    private static List<object> HazardActors()
    {
        var list = new List<object>();
        try
        {
            var actors = new List<Actor>();
            var heroes = Hero.allCollisionEnemy;
            if (heroes != null)
                for (int i = 0; i < heroes.Length; i++)
                    if (heroes[i]?.actor != null) actors.Add(heroes[i].actor);
            actors.AddRange(Recorder.RecentAttackers());
            foreach (var actor in actors)
            {
                if (list.Count >= 12) break;
                var view = actor.renderComponent?.animView;
                if (view?.anim == null) continue;
                list.Add(new
                {
                    cls = actor.GetIl2CppType().Name,
                    px = actor.px, py = actor.py,
                    anim = view.modelAnim?.id,
                    view = new { view.x, view.y, view.rotation, view.scaleX, view.scaleY, view.fps, frame = view.currentframeAsInt, view.timeBased },
                    global = Round(GlobalMatrix(view.anim)),
                    layer = view.anim.parent?.name,
                });
            }
        }
        catch (Exception e) { list.Add(new { error = e.Message }); }
        return list;
    }

    private static object Describe(DisplayObject d, int depth, int level)
    {
        var info = new Dictionary<string, object>
        {
            ["cls"] = d.className,
            ["name"] = d.name,
            ["type"] = d.GetIl2CppType().Name,
            ["visible"] = d.visible,
            ["alpha"] = MathF.Round(d.alpha, 3),
            ["xy"] = new[] { MathF.Round(d.x, 1), MathF.Round(d.y, 1) },
            ["rot"] = MathF.Round(d.rotation, 2),
            ["scale"] = new[] { MathF.Round(d.scaleX, 3), MathF.Round(d.scaleY, 3) },
        };
        try
        {
            var tr = d.transform;
            var m = tr.matrix2d;
            info["m2d"] = Round(new[] { m.a, m.b, m.c, m.d, m.tx, m.ty });
            var w = tr.concatenedMatrix;
            info["world"] = Round(new[] { w.m00, w.m01, w.m10, w.m11, w.m03, w.m13 });
            info["reg"] = Round(new[] { d.regX, d.regY });
        }
        catch (Exception e) { info["m2d"] = e.Message; }
        try
        {
            var ct = d.transform?.colorTransform;
            if (ct != null)
            {
                info["color"] = new[] { ct.color.r, ct.color.g, ct.color.b, ct.color.a };
                info["concat"] = new[] { ct.concatColor.r, ct.concatColor.g, ct.concatColor.b, ct.concatColor.a };
            }
        }
        catch { /* not every object has one */ }
        var dz = d.TryCast<DangerZoneEffect>();
        if (dz != null)
        {
            var rects = new List<float[]>();
            for (int i = 0; i < dz.numRects && i < dz.rects.Length; i++)
                rects.Add(new[] { dz.rects[i].x, dz.rects[i].y, dz.rects[i].z, dz.rects[i].w });
            info["zone"] = new { flashing = dz.flashingWarning, rects };
        }
        var mc = d.TryCast<MovieClip>();
        if (mc != null)
        {
            info["frame"] = mc.currentFrame;
            info["frames"] = mc.totalFrames;
            info["playing"] = mc.isPlaying;
            info["bundle"] = BundleOf(d);
        }
        var container = d.TryCast<DisplayObjectContainer>();
        if (container != null)
        {
            info["n"] = container.numChildren;
            if (level < depth)
            {
                var kids = new List<object>();
                foreach (var child in Children(container))
                {
                    if (kids.Count >= 40) { kids.Add("…"); break; }
                    kids.Add(Describe(child, depth, level + 1));
                }
                info["children"] = kids;
            }
        }
        return info;
    }

    private static float[] Round(float[] m) => Array.ConvertAll(m, v => MathF.Round(v, 3));
}
