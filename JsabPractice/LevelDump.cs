using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using HarmonyLib;

namespace JsabPractice;

// Writes every level the game loads to BepInEx/jsab-levels/vanilla as JSON, plus a
// catalog of all spawnable hazard types. The game's own MetaSong.encode() returns null
// in release builds, so levels are walked directly: every spawn is a hazard type,
// timing, a position and a set of key -> string params. LevelImport reads the same format.
[HarmonyPatch]
internal static class LevelDump
{
    public const int FormatVersion = 2;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static bool _catalogWritten;

    public static string RootDir => Path.Combine(Paths.BepInExRootPath, "jsab-levels");
    public static string VanillaDir => Path.Combine(RootDir, "vanilla");

    public static string LevelName(ModelStoryCheckpoint song)
    {
        var meta = song.metaSong;
        return meta != null && !string.IsNullOrEmpty(meta.name) ? meta.name : song.name;
    }

    public static string FileName(string levelName)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            levelName = levelName.Replace(c, '_');
        return levelName + ".json";
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetMetaSong))]
    private static void SetMetaSong(ModelStoryCheckpoint __instance)
    {
        if (!Plugin.DumpLevels.Value)
            return;

        try
        {
            Dump(__instance);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Level dump failed for {__instance?.name}: {e}");
        }
    }

    private static void Dump(ModelStoryCheckpoint song)
    {
        var meta = song?.metaSong;
        if (meta == null)
            return;

        Directory.CreateDirectory(VanillaDir);
        if (!_catalogWritten)
        {
            _catalogWritten = true;
            WriteCatalog();
        }

        string name = LevelName(song);
        string path = Path.Combine(VanillaDir, FileName(name));
        if (File.Exists(path))
            return;

        var spawns = meta.allSpawnsInOrder;
        var exported = new List<object>();
        for (int i = 0; spawns != null && i < spawns.Count; i++)
        {
            var spawn = spawns[i];
            if (spawn != null && !spawn.isNull())
                exported.Add(ExportSpawn(spawn));
        }

        var level = new
        {
            format = FormatVersion,
            name,
            song = song.name,
            songName = meta.songName,
            artist = meta.artistName,
            spawns = exported,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(level, Indented));
        Plugin.Logger.LogInfo($"Dumped level '{name}' ({song.name}): {Summarize(meta)} -> {path}");
    }

    private static object ExportSpawn(MetaSpawn spawn)
    {
        var group = spawn.metaParams;
        var timeline = group?.metaTimeline ?? spawn.getTimeline();
        var pos = group?.metaPosition;

        var parameters = new SortedDictionary<string, string>();
        var list = group?.metaParams;
        for (int i = 0; list != null && i < list.Count; i++)
        {
            var p = list[i];
            if (p == null || p.isNull()) continue;
            // Timeline and position are exported as their own structured fields.
            if (p.TryCast<MetaSpawnTimeline>() != null || p.TryCast<MetaSpawnParamPosition>() != null) continue;
            parameters[p.key ?? $"#{p.keyAsInt}"] = p.metaValue?.value ?? p.simpleValue;
        }

        return new
        {
            type = spawn.modelSpawn?.id,
            layer = spawn.metaLayer?.index ?? 0,
            timeMs = timeline?.timeInMs?.value,
            warningMs = timeline?.warningTime?.value,
            outroMs = timeline?.outroTime?.value,
            unique = timeline?.modelUnique?.id,
            timelineName = timeline?.name,
            pos = pos == null ? null : new
            {
                x = pos.xRelativeToScreen?.value,
                y = pos.yRelativeToScreen?.value,
                xUsed = pos.xUsed,
                yUsed = pos.yUsed,
                xRandomMargin = pos.xRandomMargin,
                yRandomMargin = pos.yRandomMargin,
            },
            @params = parameters,
        };
    }

    private static string Summarize(MetaSong meta)
    {
        var spawns = meta.allSpawnsInOrder;
        if (spawns == null)
            return "no spawn list";

        var counts = new Dictionary<string, int>();
        for (int i = 0; i < spawns.Count; i++)
        {
            string id = spawns[i]?.modelSpawn?.id ?? "?";
            counts[id] = counts.TryGetValue(id, out int n) ? n + 1 : 1;
        }

        var top = counts.OrderByDescending(kv => kv.Value).Take(6).Select(kv => $"{kv.Key}x{kv.Value}");
        return $"{spawns.Count} spawns, {counts.Count} hazard types [{string.Join(", ", top)}]";
    }

    private static void WriteCatalog()
    {
        var all = ModelSpawnEnum.ALL;
        if (all == null)
            return;

        var entries = new List<object>();
        for (int i = 0; i < all.Length; i++)
        {
            var spawn = all[i];
            if (spawn == null) continue;

            var parameters = new List<object>();
            var modelParams = spawn.modelParams;
            for (int j = 0; modelParams != null && j < modelParams.Length; j++)
            {
                var p = modelParams[j];
                if (p == null) continue;
                parameters.Add(new { id = p.id, key = p.key, type = p.modelParamType?.id, typeDefault = p.modelParamType?.defaultValue });
            }

            entries.Add(new
            {
                id = spawn.id,
                index = spawn.indexInEnum,
                frame = spawn.frame,
                isCheckPoint = spawn.isCheckPoint,
                canBeSkipped = spawn.canBeSkipped,
                warningDefaultMs = spawn.getWarningDefault(),
                parameters,
            });
        }

        string path = Path.Combine(RootDir, "_hazard-catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Indented));
        Plugin.Logger.LogInfo($"Wrote hazard catalog ({entries.Count} types) -> {path}");
    }
}
