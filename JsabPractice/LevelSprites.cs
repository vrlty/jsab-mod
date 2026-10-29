using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Assets.Plugins.lachhh.lachhhEngine.animation;

namespace JsabPractice;

// Captures every hazard look a level uses, for the editor: sprites/<LEVEL>/<anim>.active.png
// and .warning.png plus sprites.json. Hazard animations run warning -> active -> outro with
// no frame labels, so frames are sampled and scored: the most solid frame is the active
// look (warnings are outlines), the first visible frame is the warning if it's mostly outline.
internal static class LevelSprites
{
    private const int SampleEvery = 12;

    private static volatile string _progress;
    public static string Progress => _progress;

    // Main thread: work out which animations the current level needs, then sample them on
    // a worker thread, one capture at a time through SpriteCapture's queue.
    public static string Start()
    {
        if (_progress != null && !_progress.StartsWith("done") && !_progress.StartsWith("failed"))
            return EditorBridge.Error($"Already capturing ({_progress}).");

        var song = NoHit.GetArcadeLevel(quiet: true)?.currentSong;
        if (song?.metaSong == null)
            return EditorBridge.Error("Start the level in the game first; its art is only loaded while it's played.");

        string level = LevelDump.LevelName(song);
        var anims = new SortedDictionary<string, int>();
        foreach (string id in Recorder.VisualsSeen().Concat(ParamVisuals(song.metaSong)))
        {
            var model = ModelFlashAnimationEnum.getFromId(id);
            if (model == null || model.isNull || id == "HERO") continue;
            var clip = model.getMetaDisplayObject()?.TryCast<MetaMovieClip>();
            anims[id] = Math.Max(1, clip?.numFrames ?? 1);
        }

        _progress = $"0/{anims.Count}";
        Task.Run(() => Run(level, anims));
        return JsonSerializer.Serialize(new { ok = true, level, anims = anims.Keys });
    }

    // Animation names written in the level file (spawns choose their look by parameter).
    private static IEnumerable<string> ParamVisuals(MetaSong meta)
    {
        var seen = new HashSet<string>();
        var list = meta.allSpawnsInOrder;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var group = list[i]?.metaParams;
            if (group == null) continue;
            foreach (string key in new[] { "visual", "customChoice" })
            {
                if (!group.hasParamStr(key)) continue;
                string v = group.getStringStr(key);
                if (!string.IsNullOrEmpty(v) && seen.Add(v)) yield return v;
            }
        }
    }

    private sealed class Shot
    {
        public string File;
        public int Frame, Width, Height, Solid, Visible;
        public float OriginX, OriginY, PxPerArtUnit;
        public bool Clipped;
    }

    private static async Task Run(string level, SortedDictionary<string, int> anims)
    {
        string folderName = LevelDump.FileName(level).Replace(".json", "");
        string tmpFolder = Path.Combine(folderName, "_frames");
        string outDir = Path.Combine(SpriteCapture.Dir, folderName);
        var manifest = new SortedDictionary<string, object>();
        int done = 0;

        try
        {
            foreach (var (anim, frames) in anims)
            {
                _progress = $"{done}/{anims.Count} {anim}";
                float scale = 1f;
                List<Shot> shots = null;
                // Retry smaller while the art doesn't fit the capture window.
                for (int attempt = 0; attempt < 3; attempt++, scale /= 2f)
                {
                    shots = await Sample(anim, frames, scale, tmpFolder);
                    if (!shots.Any(s => s.Clipped)) break;
                }

                var visible = shots.Where(s => s.Visible > 0).ToList();
                if (visible.Count == 0)
                {
                    manifest[anim] = new { empty = true, frames };
                    done++;
                    continue;
                }

                var active = visible.OrderByDescending(s => s.Solid).ThenBy(s => s.Frame).First();
                var first = visible.OrderBy(s => s.Frame).First();
                bool hasWarning = first.Frame < active.Frame && first.Solid < first.Visible * 0.5f;

                Directory.CreateDirectory(outDir);
                manifest[anim] = new
                {
                    frames,
                    active = Keep(active, outDir, anim, "active"),
                    warning = hasWarning ? Keep(first, outDir, anim, "warning") : null,
                };
                done++;
            }

            Directory.CreateDirectory(outDir);
            File.WriteAllText(Path.Combine(outDir, "sprites.json"), JsonSerializer.Serialize(new { format = 1, level, sprites = manifest }, new JsonSerializerOptions { WriteIndented = true }));
            try { Directory.Delete(Path.Combine(SpriteCapture.Dir, tmpFolder), recursive: true); } catch { /* leftovers are harmless */ }
            _progress = $"done {done}/{anims.Count}";
            Plugin.Logger.LogInfo($"Captured sprites for '{level}': {done} animations -> {outDir}");
        }
        catch (Exception e)
        {
            _progress = $"failed: {e.Message}";
            Plugin.Logger.LogError($"Sprite capture for '{level}' failed: {e}");
        }
    }

    private static async Task<List<Shot>> Sample(string anim, int frames, float scale, string folder)
    {
        var picks = new SortedSet<int> { 1, frames };
        for (int f = SampleEvery; f < frames; f += SampleEvery) picks.Add(f);

        var shots = new List<Shot>();
        foreach (int f in picks)
        {
            // One retry: right after a frame change the game sometimes hasn't drawn yet.
            Shot shot = null;
            for (int attempt = 0; attempt < 2 && (shot == null || shot.Visible == 0); attempt++)
            {
                var req = new SpriteCapture.Request
                {
                    Mode = "anim", Visual = anim, AnimFrame = f, Scale = scale, WaitFrames = 3 + attempt * 3,
                    WindowUnits = 2000f, Folder = folder, Name = $"{anim}_{scale:0.###}_{f:000}",
                };
                shot = Parse(await SpriteCapture.Enqueue(req), f);
            }
            shots.Add(shot);
        }
        return shots;
    }

    private static Shot Parse(string json, int frame)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var shot = new Shot { Frame = frame };
        if (!r.TryGetProperty("ok", out var ok) || !ok.GetBoolean() || r.TryGetProperty("empty", out _))
            return shot;
        shot.File = r.GetProperty("file").GetString();
        shot.Width = r.GetProperty("width").GetInt32();
        shot.Height = r.GetProperty("height").GetInt32();
        shot.OriginX = r.GetProperty("originX").GetSingle();
        shot.OriginY = r.GetProperty("originY").GetSingle();
        shot.PxPerArtUnit = r.GetProperty("pxPerArtUnit").GetSingle();
        shot.Clipped = r.GetProperty("clipped").GetBoolean();
        shot.Solid = r.GetProperty("solid").GetInt32();
        shot.Visible = r.GetProperty("visible").GetInt32();
        return shot;
    }

    private static object Keep(Shot s, string outDir, string anim, string kind)
    {
        string name = $"{LevelDump.FileName(anim).Replace(".json", "")}.{kind}.png";
        File.Copy(s.File, Path.Combine(outDir, name), overwrite: true);
        return new { file = name, frame = s.Frame, width = s.Width, height = s.Height, originX = s.OriginX, originY = s.OriginY, pxPerArtUnit = s.PxPerArtUnit, solid = s.Solid, visible = s.Visible };
    }
}
