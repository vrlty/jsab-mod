using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx;
using HarmonyLib;

namespace JsabPractice;

// Writes every level the game loads to BepInEx/jsab-levels as JSON, plus a catalog of
// all spawnable hazard types. The game's own MetaSong.encode() returns null in release
// builds, so levels are walked directly: every spawn is a hazard type, a layer, timing,
// a position and a set of key -> string params. Groundwork for the level editor.
[HarmonyPatch]
internal static class LevelDump
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static bool _catalogWritten;
    private static bool _probed;

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

        if (!_probed)
        {
            _probed = true;
            ProbeGameEncoders(meta);
        }

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
            lengthMs = meta.songLength,
            layers = meta.metaLayerGroup?.metaLayers?.Count ?? 0,
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
            // The game's own level-editor record for this spawn; MetaSpawn.createWithDict
            // reads this format back, so it's what an importer should feed the game.
            editor = EditorRecord(spawn),
        };
    }

    private static JsonElement? EditorRecord(MetaSpawn spawn)
    {
        try
        {
            var dict = spawn.encodeForLevelEditor();
            if (dict == null)
                return null;
            using var doc = JsonDocument.Parse(MiniJSON.Json.Serialize(dict));
            return doc.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Whether the game's own encoders work tells us if decode() is usable for importing.
    private static void ProbeGameEncoders(MetaSong meta)
    {
        void Try(string what, Func<Il2CppSystem.Object> encode)
        {
            try
            {
                var obj = encode();
                string json = obj == null ? "<null object>" : MiniJSON.Json.Serialize(obj);
                Plugin.Logger.LogInfo($"[diag] {what}: {(json.Length > 300 ? json.Substring(0, 300) + "..." : json)}");
            }
            catch (Exception e)
            {
                Plugin.Logger.LogInfo($"[diag] {what} threw {e.GetType().Name}: {e.Message}");
            }
        }

        Try("MetaSong.encode", () => meta.encode());
        var first = meta.allSpawnsInOrder is { Count: > 0 } s ? s[0] : null;
        if (first == null)
            return;
        Try("MetaSpawn.encode", () => first.encode());
        Try("MetaSpawn.encodeForLevelEditor", () => first.encodeForLevelEditor());
        Try("MetaSpawnParamGroup.saveData", () => first.metaParams?.saveData);
        Try("MetaSpawnParamGroup.encode", () => first.metaParams?.encode());
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
        File.WriteAllText(path, JsonSerializer.Serialize(entries, Indented));
        Plugin.Logger.LogInfo($"Wrote hazard catalog ({entries.Count} types) -> {path}");
    }

    private static string Sanitize(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
