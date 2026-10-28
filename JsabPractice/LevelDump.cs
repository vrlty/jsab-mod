using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using HarmonyLib;

namespace JsabPractice;

// Writes every level the game loads to BepInEx/jsab-levels as JSON, using the game's
// own MetaSong.encode() + MiniJSON, plus a catalog of all spawnable hazard types.
// This is the groundwork for the level editor: same format in, same format out.
[HarmonyPatch]
internal static class LevelDump
{
    private static bool _catalogWritten;

    public static string OutputDir => Path.Combine(Paths.BepInExRootPath, "jsab-levels");

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

        Directory.CreateDirectory(OutputDir);
        if (!_catalogWritten)
        {
            _catalogWritten = true;
            WriteCatalog();
        }

        string name = string.IsNullOrEmpty(meta.name) ? song.name : meta.name;
        string path = Path.Combine(OutputDir, Sanitize(name) + ".json");
        if (File.Exists(path))
            return;

        string json = MiniJSON.Json.Serialize(meta.encode());
        File.WriteAllText(path, Pretty(json));
        Plugin.Logger.LogInfo($"Dumped level '{name}' ({song.name}): {Summarize(meta)} -> {path}");
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
        return $"{spawns.Count} spawns, {counts.Count} hazard types, length {meta.songLength:0}ms [{string.Join(", ", top)}]";
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

        string path = Path.Combine(OutputDir, "_hazard-catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        Plugin.Logger.LogInfo($"Wrote hazard catalog ({entries.Count} types) -> {path}");
    }

    // MiniJSON writes everything on one line; indent it so the files are readable.
    // If it doesn't parse as standard JSON, keep it as-is rather than lose it.
    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException e)
        {
            Plugin.Logger.LogWarning($"Level JSON isn't standard JSON ({e.Message}); writing it raw.");
            return json;
        }
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
