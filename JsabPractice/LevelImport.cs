using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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

    // The vanilla spawn list of every level we've replaced, so a reload can put it back
    // when the custom file is deleted.
    private static readonly Dictionary<IntPtr, List<MetaSpawn>> Originals = new();

    // What each level is currently playing: "vanilla", or "custom:" + a hash of the file.
    // Motion recordings carry it so the editor can tell whether one matches what it shows.
    private static readonly Dictionary<string, string> Signatures = new(StringComparer.OrdinalIgnoreCase);

    public static string SignatureOf(string level) => Signatures.TryGetValue(level, out var s) ? s : "vanilla";

    // Low priority: run after LevelDump so the vanilla dump is never the custom level.
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Low)]
    [HarmonyPatch(typeof(ModelStoryCheckpoint), nameof(ModelStoryCheckpoint.SetMetaSong))]
    private static void SetMetaSong(ModelStoryCheckpoint __instance)
    {
        // Unattended recording captures the game's own levels, not custom files.
        if (Plugin.CustomLevels.Value && !AutoRecorder.Active)
            TryApply(__instance, restoreIfMissing: false);
    }

    // Applies the custom file for this song if there is one. With restoreIfMissing, a
    // song without a custom file gets its original spawns back (used by hot reload).
    // Returns true if the level's spawns were changed.
    public static bool TryApply(ModelStoryCheckpoint song, bool restoreIfMissing)
    {
        var meta = song?.metaSong;
        if (meta == null)
            return false;

        string name = LevelDump.LevelName(song);
        string path = Path.Combine(CustomDir, LevelDump.FileName(name));

        if (MultiplayerRoom.Instance != null)
        {
            if (File.Exists(path))
                Plugin.Logger.LogWarning($"Multiplayer room is active; not loading custom level for '{name}'.");
            return false;
        }

        try
        {
            if (File.Exists(path))
                return Apply(meta, name, path);
            if (restoreIfMissing)
                return Restore(meta, name);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Custom level '{name}' failed to load, keeping the current one: {e}");
        }
        return false;
    }

    private static bool Restore(MetaSong meta, string name)
    {
        if (!Originals.TryGetValue(meta.Pointer, out var original))
            return false;

        ReplaceSpawns(meta, original);
        Originals.Remove(meta.Pointer);
        Signatures.Remove(name);
        Plugin.Logger.LogInfo($"No custom file for '{name}'; restored the original level ({original.Count} spawns).");
        return true;
    }

    private static void ReplaceSpawns(MetaSong meta, List<MetaSpawn> spawns)
    {
        var list = meta.allSpawnsInOrder;
        list.Clear();
        foreach (var spawn in spawns)
            list.Add(spawn);
        meta.sortByTimeAndSetIndex();
        meta.feedCheckPointList();
    }

    private static bool Apply(MetaSong meta, string name, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        using var doc = JsonDocument.Parse(bytes, Lenient);
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
            Plugin.Logger.LogError($"Custom level '{name}' has no valid spawns; keeping the current one.");
            return false;
        }

        if (!Originals.ContainsKey(meta.Pointer))
        {
            var current = meta.allSpawnsInOrder;
            var original = new List<MetaSpawn>(current.Count);
            for (int i = 0; i < current.Count; i++)
                original.Add(current[i]);
            Originals[meta.Pointer] = original;
        }

        ReplaceSpawns(meta, created);
        Signatures[name] = "custom:" + Convert.ToHexString(SHA256.HashData(bytes)).Substring(0, 16).ToLowerInvariant();
        Plugin.Logger.LogInfo($"Loaded custom level '{name}': {created.Count} spawns ({skipped} skipped) from {path}");
        return true;
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
