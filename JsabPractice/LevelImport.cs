using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using HarmonyLib;
using Il2CppSystem.Collections;

namespace JsabPractice;

// Replaces a level's spawns with BepInEx/jsab-levels/custom/<LEVEL>.json when that level
// loads. The file uses the same format LevelDump writes; each spawn is converted back to
// the game's own level-editor record and built with MetaSpawn.createWithDict.
[HarmonyPatch]
internal static class LevelImport
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string CustomDir => Path.Combine(LevelDump.RootDir, "custom");

    // Low priority: run after LevelDump so the vanilla dump is never the custom level.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetMetaSong))]
    private static void SetMetaSong(ModelStoryCheckpoint __instance)
    {
        if (!Plugin.CustomLevels.Value || __instance?.metaSong == null)
            return;

        string name = LevelDump.LevelName(__instance);
        string path = Path.Combine(CustomDir, LevelDump.FileName(name));
        if (!File.Exists(path))
            return;

        if (MultiplayerRoom.Instance != null)
        {
            Plugin.Logger.LogWarning($"Multiplayer room is active; not loading custom level for '{name}'.");
            return;
        }

        try
        {
            Apply(__instance.metaSong, name, path);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Custom level '{name}' failed to load, keeping the original: {e}");
        }
    }

    private static void Apply(MetaSong meta, string name, string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), Lenient);
        if (!doc.RootElement.TryGetProperty("spawns", out var spawns) || spawns.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("no \"spawns\" array");

        // Build everything first so a broken file can't leave the level half-replaced.
        var created = new List<MetaSpawn>();
        int index = 0, skipped = 0;
        foreach (var s in spawns.EnumerateArray())
        {
            index++;
            try
            {
                var spawn = CreateSpawn(s);
                if (spawn != null)
                    created.Add(spawn);
                else
                    skipped++;
            }
            catch (Exception e)
            {
                skipped++;
                Plugin.Logger.LogWarning($"Custom level '{name}': spawn #{index} skipped: {e.Message}");
            }
        }

        if (created.Count == 0)
        {
            Plugin.Logger.LogError($"Custom level '{name}' has no valid spawns; keeping the original.");
            return;
        }

        var list = meta.allSpawnsInOrder;
        list.Clear();
        foreach (var spawn in created)
            list.Add(spawn);
        meta.sortByTimeAndSetIndex();
        meta.feedCheckPointList();

        Plugin.Logger.LogInfo($"Loaded custom level '{name}': {created.Count} spawns ({skipped} skipped) from {path}");
    }

    private static MetaSpawn CreateSpawn(JsonElement s)
    {
        string type = Str(s, "type");
        var model = type == null ? null : ModelSpawnEnum.getFromId(type);
        if (model == null || model.isNull)
            throw new InvalidDataException($"unknown hazard type '{type}'");

        var parameters = new JsonObject
        {
            ["timeline"] = new JsonObject
            {
                ["modelUnique"] = Str(s, "unique") ?? "unique1",
                ["timeInMs"] = Str(s, "timeMs") ?? "0",
                ["outroTime"] = Str(s, "outroMs") ?? "0",
                ["warningTime"] = Str(s, "warningMs") ?? "0",
                ["name"] = Str(s, "timelineName") ?? "",
            },
        };

        // The game only stores a position for hazard types that have a pos parameter.
        var posParam = model.getModelParamByKey("pos");
        if (posParam != null && !posParam.isNull)
        {
            s.TryGetProperty("pos", out var pos);
            string x = Str(pos, "x") ?? "", y = Str(pos, "y") ?? "";
            parameters["pos"] = new JsonObject
            {
                ["xRandomMargin"] = Num(pos, "xRandomMargin"),
                ["xUsed"] = Bool(pos, "xUsed") ?? x.Length > 0,
                ["yUsed"] = Bool(pos, "yUsed") ?? y.Length > 0,
                ["yRandomMargin"] = Num(pos, "yRandomMargin"),
                ["y"] = y,
                ["x"] = x,
            };
        }

        if (s.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ps.EnumerateObject())
                parameters[p.Name] = new JsonObject { ["value"] = AsString(p.Value) };
        }

        var record = new JsonObject
        {
            ["params"] = parameters,
            ["layerId"] = "-1",
            ["modelSpawn"] = type,
        };

        var dict = MiniJSON.Json.Deserialize(record.ToJsonString())?.TryCast<Hashtable>();
        if (dict == null)
            throw new InvalidDataException("game JSON parser rejected the spawn record");

        var spawn = MetaSpawn.createWithDict(dict);
        return spawn == null || spawn.isNull() ? null : spawn;
    }

    // Values are strings in the game's format, but accept plain numbers/bools too since
    // that's what people will naturally type when editing by hand.
    private static string AsString(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    private static string Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) ? AsString(v) : null;

    private static double Num(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool? Bool(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : null;
}
