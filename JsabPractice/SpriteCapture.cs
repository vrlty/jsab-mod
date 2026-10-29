using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assets.Plugins.lachhh.lachhhEngine.animation;
using UnityEngine;

namespace JsabPractice;

// Renders a hazard's real appearance with the game's own camera, isolated far off the
// playfield, and saves it as a PNG with its anchor point, for the level editor.
//
// Ways to create what's captured (which one works best is still being explored):
//   anim    - just the Flash animation named by Visual (ModelFlashAnimationEnum)
//   preview - MetaSpawn.createSpawnPreview() of the first spawn of Type in the level
//   spawn   - MetaSpawn.createSpawn(), the real hazard
internal static class SpriteCapture
{
    public sealed class Request
    {
        public string Name;
        public string Type;
        public string Visual;
        public string Mode = "anim";
        public float OffsetX = 30000f;
        public float OffsetY = 0f;
        public int WaitFrames = 2;
        public int AnimFrame = 1;
        public string Label;
        public bool Play;
        public float WindowUnits = 700f;
        public string Folder = "_test";
    }

    private sealed class Job
    {
        public Request Req;
        public readonly TaskCompletionSource<string> Done = new();
        public int Frame;
        public FlashAnimation Anim;
        public Spawn Spawn;
        public object Info;
    }

    // What was rendered: the animation a hazard uses is often hard-coded in its class.
    private static object Describe(Job job)
    {
        try
        {
            if (job.Anim != null)
                return new { anim = job.Req.Visual, frame = job.Anim.currentFrame, totalFrames = job.Anim.totalFrames, labels = Labels(job.Req.Visual) };
            var view = job.Spawn?.renderComponent?.animView;
            return new { cls = job.Spawn?.GetIl2CppType().Name, anim = view?.modelAnim?.id, frame = view?.anim?.currentFrame, totalFrames = view?.anim?.totalFrames };
        }
        catch (Exception e)
        {
            return new { describeError = e.Message };
        }
    }

    private static readonly Queue<Job> Jobs = new();
    private static Job _job;

    public static string Dir => Path.Combine(LevelDump.RootDir, "sprites");

    // Called from the bridge's worker thread; the work happens across frames in Update.
    public static Task<string> Enqueue(Request req)
    {
        var job = new Job { Req = req };
        lock (Jobs) Jobs.Enqueue(job);
        return job.Done.Task;
    }

    public static void Update()
    {
        if (_job == null)
        {
            lock (Jobs)
            {
                if (Jobs.Count == 0) return;
                _job = Jobs.Dequeue();
            }
        }

        var job = _job;
        try
        {
            if (job.Frame == 0)
                Create(job);
            if (job.Frame >= job.Req.WaitFrames)
            {
                job.Done.TrySetResult(Render(job));
                Cleanup(job);
                _job = null;
                return;
            }
            job.Frame++;
        }
        catch (Exception e)
        {
            Cleanup(job);
            job.Done.TrySetResult(EditorBridge.Error($"{e.GetType().Name}: {e.Message}"));
            _job = null;
        }
    }

    private static void Create(Job job)
    {
        var r = job.Req;
        switch (r.Mode)
        {
            case "anim":
            {
                var model = ModelFlashAnimationEnum.getFromId(r.Visual);
                if (model == null || model.isNull)
                    throw new InvalidOperationException($"no animation '{r.Visual}'");
                job.Anim = model.createFlashInstance();
                // Hazards live in the gameplay layers, which use world coordinates; the
                // stage root uses screen coordinates.
                DisplayObjectContainer layer = CameraFlashContainers.instance?.enemyVisual ?? MainGame.stage;
                layer.addChild(job.Anim);
                job.Anim.x = r.OffsetX;
                job.Anim.y = r.OffsetY;
                // Animations run warning -> active -> outro; labels name those parts, and
                // playing lets nested clips reach their own active frames.
                if (r.Label != null)
                {
                    if (r.Play) job.Anim.gotoAndPlay(r.Label);
                    else job.Anim.gotoAndStop(r.Label);
                }
                else if (r.Play) job.Anim.gotoAndPlay(r.AnimFrame);
                else job.Anim.gotoAndStop(r.AnimFrame);
                break;
            }
            case "preview":
            case "spawn":
            {
                var meta = FindMetaSpawn(r.Type, r.Visual)
                    ?? throw new InvalidOperationException($"no '{r.Type}' spawn in the current level");
                job.Spawn = r.Mode == "preview" ? meta.createSpawnPreview() : meta.createSpawn();
                if (job.Spawn == null)
                    throw new InvalidOperationException("the game returned no spawn");
                job.Spawn.setPos(r.OffsetX, r.OffsetY);
                break;
            }
            default:
                throw new ArgumentException($"unknown mode '{r.Mode}'");
        }
    }

    // "label@frame" for every named frame of an animation.
    public static string[] Labels(string animId)
    {
        var clip = ModelFlashAnimationEnum.getFromId(animId)?.getMetaDisplayObject()?.TryCast<MetaMovieClip>();
        if (clip?.frameLabels == null) return Array.Empty<string>();
        var result = new List<string>();
        for (int i = 0; i < clip.frameLabels.Count; i++)
        {
            int frame = clip.frameLabelsId != null && i < clip.frameLabelsId.Count ? clip.frameLabelsId[i] : -1;
            result.Add($"{clip.frameLabels[i]}@{frame}");
        }
        return result.ToArray();
    }

    private static MetaSpawn FindMetaSpawn(string type, string visual)
    {
        var song = NoHit.GetArcadeLevel(quiet: true)?.currentSong?.metaSong;
        var list = song?.allSpawnsInOrder;
        if (list == null) return null;
        MetaSpawn fallback = null;
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            if (s?.modelSpawn?.id != type) continue;
            fallback ??= s;
            if (visual == null) return s;
            var group = s.metaParams;
            if (group != null && group.hasParamStr("visual") && group.getStringStr("visual") == visual) return s;
        }
        return fallback;
    }

    private static string Render(Job job)
    {
        var r = job.Req;
        var cam = MainGame.stage?.gettk2dCamera() ?? throw new InvalidOperationException("no stage camera");
        int w = Screen.width, h = Screen.height;
        var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
        var prevTarget = cam.targetTexture;
        var prevPos = cam.transform.position;
        var prevActive = RenderTexture.active;
        Texture2D tex = null;
        try
        {
            cam.transform.position = new Vector3(r.OffsetX, r.OffsetY, prevPos.z);
            cam.targetTexture = rt;
            cam.Render();

            // Pixel scale and where the anchor lands, measured through the camera itself.
            Vector3 c = cam.WorldToScreenPoint(new Vector3(r.OffsetX, r.OffsetY, 0f));
            Vector3 cx = cam.WorldToScreenPoint(new Vector3(r.OffsetX + 100f, r.OffsetY, 0f));
            float pxPerUnit = Mathf.Abs(cx.x - c.x) / 100f;
            job.Info = Describe(job);

            int half = Mathf.Clamp(Mathf.RoundToInt(r.WindowUnits * pxPerUnit / 2f), 16, Math.Min(w, h) / 2);
            int x0 = Mathf.Clamp(Mathf.RoundToInt(c.x) - half, 0, w - 2 * half);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(c.y) - half, 0, h - 2 * half);
            RenderTexture.active = rt;
            tex = new Texture2D(2 * half, 2 * half, TextureFormat.RGBA32, false);
            tex.ReadPixels(new Rect(x0, y0, 2 * half, 2 * half), 0, 0);
            tex.Apply();

            return Save(r, tex, c.x - x0, c.y - y0, pxPerUnit, job.Info);
        }
        finally
        {
            cam.targetTexture = prevTarget;
            cam.transform.position = prevPos;
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            if (tex != null) UnityEngine.Object.Destroy(tex);
        }
    }

    // Crop to whatever differs from the background, key the background out, and save.
    // Texture rows run bottom to top; the PNG and the reported origin are top-down.
    private static string Save(Request r, Texture2D tex, float anchorX, float anchorY, float pxPerUnit, object info)
    {
        int size = tex.width;
        var px = tex.GetPixels32();
        var bg = px[0];
        int minX = size, minY = size, maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            var p = px[y * size + x];
            if (Math.Abs(p.r - bg.r) + Math.Abs(p.g - bg.g) + Math.Abs(p.b - bg.b) <= 12) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        if (maxX < 0)
            return JsonSerializer.Serialize(new { ok = true, empty = true, name = r.Name, bg = $"{bg.r},{bg.g},{bg.b}", pxPerUnit, info });

        const int pad = 2;
        minX = Math.Max(0, minX - pad); minY = Math.Max(0, minY - pad);
        maxX = Math.Min(size - 1, maxX + pad); maxY = Math.Min(size - 1, maxY + pad);
        int cw = maxX - minX + 1, ch = maxY - minY + 1;

        var outTex = new Texture2D(cw, ch, TextureFormat.RGBA32, false);
        var outPx = new Color32[cw * ch];
        int solid = 0, visible = 0;
        for (int y = 0; y < ch; y++)
        for (int x = 0; x < cw; x++)
        {
            var p = px[(minY + y) * size + minX + x];
            // Rendered over black: brightness becomes opacity, colour is un-premultiplied.
            int m = Math.Max(p.r, Math.Max(p.g, p.b));
            byte a = (byte)Math.Min(255, m * 3);
            if (a == 0) { outPx[y * cw + x] = new Color32(0, 0, 0, 0); continue; }
            visible++;
            if (a == 255) solid++;
            float k = 255f / Math.Max(m, 1);
            outPx[y * cw + x] = m >= 85
                ? new Color32(p.r, p.g, p.b, 255)
                : new Color32((byte)Math.Min(255, p.r * k), (byte)Math.Min(255, p.g * k), (byte)Math.Min(255, p.b * k), a);
        }
        outTex.SetPixels32(outPx);
        outTex.Apply();
        byte[] png = ImageConversion.EncodeToPNG(outTex).ToArray();
        UnityEngine.Object.Destroy(outTex);

        string folder = Path.Combine(Dir, r.Folder);
        Directory.CreateDirectory(folder);
        string name = string.IsNullOrEmpty(r.Name) ? (r.Visual ?? r.Type ?? "capture") : r.Name;
        string file = Path.Combine(folder, LevelDump.FileName(name).Replace(".json", ".png"));
        File.WriteAllBytes(file, png);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            file,
            width = cw,
            height = ch,
            // Anchor (the hazard's position) in image pixels from the top-left.
            originX = anchorX - minX,
            originY = (ch - 1) - (anchorY - minY),
            pxPerUnit,
            bg = $"{bg.r},{bg.g},{bg.b}",
            // Filled pixels vs any visible ones: warnings are thin outlines, active hazards solid.
            solid,
            visible,
            info,
        });
    }

    private static void Cleanup(Job job)
    {
        try
        {
            if (job.Anim != null)
            {
                job.Anim.parent?.removeChild(job.Anim);
                job.Anim = null;
            }
            if (job.Spawn != null)
            {
                job.Spawn.destroy();
                job.Spawn = null;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Capture cleanup failed: {e.Message}");
        }
    }

    public static Request Parse(JsonElement root)
    {
        var r = new Request();
        string S(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        float? F(string k) => root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : null;
        r.Name = S("name");
        r.Type = S("type");
        r.Visual = S("visual");
        r.Mode = S("mode") ?? r.Mode;
        r.Folder = S("folder") ?? r.Folder;
        r.Label = S("label");
        r.Play = root.TryGetProperty("play", out var play) && play.ValueKind == JsonValueKind.True;
        r.OffsetX = F("offsetX") ?? r.OffsetX;
        r.OffsetY = F("offsetY") ?? r.OffsetY;
        r.WaitFrames = (int)(F("waitFrames") ?? r.WaitFrames);
        r.AnimFrame = (int)(F("animFrame") ?? r.AnimFrame);
        r.WindowUnits = F("windowUnits") ?? r.WindowUnits;
        return r;
    }
}
