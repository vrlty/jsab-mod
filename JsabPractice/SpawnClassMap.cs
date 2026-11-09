using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UnityEngine;

namespace JsabPractice;

// Which game class each spawn type really is. A level's spawn type (ModelSpawn.id, e.g.
// "BeamFromAnywhere") often isn't its class's name ("Beam"), and the game builds them through
// lambdas, so the map is taken from what's alive: every Spawn in the scene's managers, by its
// modelSpawn.id, its runtime class and that class's bases up to Spawn. Merged into
// <levels folder>/_spawn-classes.json:
//   { "<type>": { "class": "<Class>", "bases": ["<Base>", ..., "Spawn"], "levels": ["<LEVEL>", ...] } }
// A type seen as more than one class keeps the first and logs the other.
internal static class SpawnClassMap
{
    private const float SaveEverySeconds = 5f;

    private sealed class Entry
    {
        public string Class { get; set; }
        public List<string> Bases { get; set; } = new();
        public List<string> Levels { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static Dictionary<string, Entry> _map;
    private static bool _dirty;
    private static float _nextScan, _nextSave;

    private static string FilePath => Path.Combine(LevelDump.RootDir, "_spawn-classes.json");

    // Actors already looked at, by (actor, its MetaSpawn): pooled actors come back as other spawns.
    private static readonly HashSet<(IntPtr, IntPtr)> Seen = new();

    // Called every frame (PracticeRunner.Update): effect spawns can live a single frame.
    public static void Update()
    {
        float now = Time.unscaledTime;
        if (_dirty && now >= _nextSave) Save();
        if (now < _nextScan) return;
        try { Scan(); }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"[spawn-classes] scan failed: {e.Message}");
            _nextScan = now + 30f;
        }
    }

    private static void Scan()
    {
        var managers = GameScene.instance?.allManagers;
        if (managers == null) return;
        var song = NoHit.GetArcadeLevel(quiet: true)?.currentSong;
        if (song == null) return;
        _map ??= Load();
        string level = null;
        for (int m = 0; m < managers.Length; m++)
        {
            var actors = managers[m]?.actorList;
            if (actors == null) continue;
            for (int i = 0; i < actors.Count; i++)
            {
                var spawn = actors[i]?.TryCast<Spawn>();
                var meta = spawn?.metaSpawn;
                if (meta == null || !Seen.Add((spawn.Pointer, meta.Pointer))) continue;
                if (Seen.Count > 100000) Seen.Clear();
                string type = meta.modelSpawn?.id;
                if (string.IsNullOrEmpty(type)) continue;
                var il2cppType = spawn.GetIl2CppType();
                string cls = il2cppType.Name;
                level ??= LevelDump.LevelName(song);
                if (!_map.TryGetValue(type, out var entry))
                {
                    entry = new Entry { Class = cls, Bases = BasesOf(il2cppType) };
                    _map[type] = entry;
                    _dirty = true;
                    Plugin.Logger.LogInfo($"[spawn-classes] {type} -> {cls} ({string.Join(" < ", entry.Bases)})");
                }
                else if (entry.Class != cls)
                    Plugin.Logger.LogWarning($"[spawn-classes] {type} is also {cls} (kept {entry.Class})");
                if (!entry.Levels.Contains(level)) { entry.Levels.Add(level); _dirty = true; }
            }
        }
    }

    private static List<string> BasesOf(Il2CppSystem.Type t)
    {
        var bases = new List<string>();
        for (var b = t.BaseType; b != null && bases.Count < 12; b = b.BaseType)
        {
            bases.Add(b.Name);
            if (b.Name == "Spawn") break;
        }
        return bases;
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[spawn-classes] couldn't read {FilePath}: {e.Message}"); }
        return new();
    }

    public static void Save()
    {
        if (!_dirty || _map == null) return;
        _dirty = false;
        _nextSave = Time.unscaledTime + SaveEverySeconds;
        try
        {
            var sorted = _map.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(sorted, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"[spawn-classes] couldn't save: {e.Message}"); }
    }
}
