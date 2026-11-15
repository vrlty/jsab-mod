using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Assets.Plugins.Berzerk;
using Assets.Plugins.lachhh.lachhhEngine.animation;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using NVorbis;
using UnityEngine;

namespace JsabPractice;

// Level packages: <Folder>/packages/*.jsab, a zip with level.json and, for a new song,
// music.ogg. Each package becomes its own song at the end of the practice list (offline
// only: multiplayer reads the same list, so custom songs never reach it):
//   level.json { "format": 1, "id": "my-level", "name": "Song", "artist": "Who made the music",
//                "author": "Who made the level", "difficulty": 2,
//                "remixOf": "MUSIC_FOX",            // optional: play that song's music instead
//                "normal": { "spawns": [...] }, "hard": { "spawns": [...] } }   // hard optional
// Spawns use the same format as custom/<LEVEL>.json. The level ends at its last CheckPoint.
//
// How the game loads a song (decompiled): LogicLoadMetaSongAudioOfModelSong reads the level
// (MetaSong) and music (AudioClip) from the song's asset bundle and hands them to
// ModelStoryCheckpoint.SetMetaSong / SetAudioClip, while LogicLoadAllAssetOfModelFlaSimul
// loads the art bundles in ModelSoundTrack.flasToLoad. For a package song the first step is
// replaced: the level is built from level.json and the music decoded from music.ogg (a remix
// takes its original's music from the original's bundle). Its art list is worked out from
// the hazards it uses (hazard-art.json, from tools/hazard_art.py). The game's build strips
// Unity's audio file loader (UnityWebRequestMultimedia.GetAudioClip fails with a missing
// DownloadHandlerAudioClip constructor), so the Ogg is decoded here with NVorbis on a worker
// thread and handed to AudioClip.Create/SetData.
[HarmonyPatch]
internal static class CustomSongs
{
    public const int FormatVersion = 1;
    private const long MaxLevelBytes = 16L << 20;
    private const long MaxMusicBytes = 128L << 20;
    private const int ClipsKept = 2;
    private const int MaxMusicSeconds = 20 * 60;
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9._-]{0,63}$");
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string PackagesDir => Path.Combine(LevelDump.RootDir, "packages");

    private sealed class Song
    {
        public string Id, File, Name, Artist, Author, RemixOf;
        public int Difficulty;
        public DateTime Stamp;
        public HashSet<string> Art = new();
        public ModelStoryCheckpoint Original;   // the remixed song
        public ModelStoryCheckpoint Checkpoint;
        public MetaSong Meta;
        public AudioClip Clip;                  // decoded music.ogg (new songs only)
        public DateTime ClipStamp;
        public string LevelName => "CUSTOM_" + Id;
    }

    private sealed class PendingLoad
    {
        public Song Song;
        public Callback Callback;
        public Task<Pcm> Decode;
    }

    private sealed class Pcm
    {
        public float[] Samples;   // interleaved
        public int Channels, Rate;
    }

    private static readonly Dictionary<string, Song> Songs = new(StringComparer.Ordinal);
    private static readonly Dictionary<IntPtr, Song> ByCheckpoint = new();
    private static readonly Dictionary<string, MetaArcadeSongProgress> Progress = new(StringComparer.Ordinal);
    private static readonly List<Song> LoadedClips = new();
    private static PendingLoad _pending;
    private static Dictionary<string, string[]> _hazardArt;

    public static bool IsCustom(ModelStoryCheckpoint song) => Find(song) != null;

    private static Song Find(ModelStoryCheckpoint song) =>
        song != null && ByCheckpoint.TryGetValue(song.Pointer, out var s) ? s : null;

    // ---- the practice list --------------------------------------------------------------

    // The practice list (ViewPlaylistSongList's constructor) copies ALL_MULTI into its own
    // metaSongList and calls createSongView for each entry, re-reading the list's length every
    // step. When the row for ALL_MULTI's last song is being made, package songs are appended to
    // that list, so the loop makes their rows too: they land at the end of the practice list
    // and ALL_MULTI itself (which multiplayer reads) never holds them. Hooks on the constructor
    // or on UI_Practice.create never fire (constructors can't be patched in this game, and the
    // menu doesn't call create), so createSongView is the reliable place. The packages folder
    // is read each time, so a new or changed package shows up by reopening the menu.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ViewPlaylistSongList), nameof(ViewPlaylistSongList.createSongView))]
    private static void CreatingSongRow(ViewPlaylistSongList __instance, ModelStoryCheckpoint __0)
    {
        try
        {
            if (!Plugin.CustomLevels.Value || MultiplayerRoom.Instance != null || __0 == null)
                return;
            var allMulti = ModelStoryCheckpointEnum.ALL_MULTI;
            var list = __instance.metaSongList?.modelSongList;
            if (allMulti == null || list == null || allMulti.Length == 0 || list.Length != allMulti.Length
                || allMulti[allMulti.Length - 1]?.Pointer != __0.Pointer)
                return;
            if (list.Pointer == allMulti.Pointer)
            {
                Plugin.Logger.LogError("Practice list shares the game's song list; package songs not added (they would reach multiplayer).");
                return;
            }

            Scan();
            int before = list.Length;
            foreach (var song in Songs.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
                if (song.Checkpoint != null)
                    list.push(song.Checkpoint);
            Plugin.Logger.LogInfo($"Practice list: {list.Length - before} package songs added at the end");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Adding package songs to the practice list failed: {e}");
        }
    }

    // The practice menu previews the selected song with a preview clip and preview level from
    // the game's resources (ModelStoryCheckpoint.loadSongPreviewSound). Package songs have
    // neither: PreviewSfx.play throws and LogicSpawnCreatorPreview.update then throws every
    // frame. They get no preview; selecting one just stops the current one.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UI_Practice), nameof(UI_Practice.StartPreviewForReal))]
    private static bool StartPreview(UI_Practice __instance)
    {
        if (!IsCustom(__instance.currentSong))
            return true;
        __instance.m_timerToStartPreview = null;
        __instance.StopPreview();
        return false;
    }

    // A rewind destroys everything on screen and only respawns spawns starting after the
    // checkpoint, so hazards spawned before it and still up are lost. The game restores those
    // (LogicCloneObjectsOnRewind: clones their spawns at the checkpoint, recreates them after
    // the rewind) but only when the song is ModelSoundTrackEnum.TOKYO_SKIES. Package songs get
    // the same: while those two run for one, TOKYO_SKIES points at the package song's track.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicCloneObjectsOnRewind), nameof(LogicCloneObjectsOnRewind.onCheckpoint))]
    private static void CloningAtCheckpoint(LogicCloneObjectsOnRewind __instance, out ModelSoundTrack __state) =>
        __state = PretendTokyoSkies(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(LogicCloneObjectsOnRewind), nameof(LogicCloneObjectsOnRewind.onCheckpoint))]
    private static void ClonedAtCheckpoint(LogicCloneObjectsOnRewind __instance, ModelSoundTrack __state)
    {
        RestoreTokyoSkies(__state);
        try
        {
            int kept = __instance.listToClone?.Count ?? 0;
            if (__state != null || kept > 0)
                Plugin.Logger.LogInfo($"Checkpoint: {kept} attacks kept for a rewind{(__state != null ? " (package song)" : "")}");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"Counting kept attacks failed: {e.Message}"); }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicCloneObjectsOnRewind), nameof(LogicCloneObjectsOnRewind.onRewindFinished))]
    private static void RestoringAfterRewind(LogicCloneObjectsOnRewind __instance, out ModelSoundTrack __state) =>
        __state = PretendTokyoSkies(__instance);

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(LogicCloneObjectsOnRewind), nameof(LogicCloneObjectsOnRewind.onRewindFinished))]
    private static void RestoredAfterRewind(ModelSoundTrack __state) => RestoreTokyoSkies(__state);

    // The squares only carry the component LogicCloneObjectsOnRewind collects when
    // FxSquareAppearPreParadise.isSongTokyoSkies says so (customCodeIfSongIsTokyoSkies).
    [HarmonyPostfix]
    [HarmonyPatch(typeof(FxSquareAppearPreParadise), nameof(FxSquareAppearPreParadise.isSongTokyoSkies))]
    private static void SquareInTokyoSkies(ref bool __result)
    {
        if (!__result && IsCustom(NoHit.GetArcadeLevel(quiet: true)?.currentSong))
            __result = true;
    }

    // Returns the real TOKYO_SKIES to put back, or null when nothing was changed.
    private static ModelSoundTrack PretendTokyoSkies(LogicCloneObjectsOnRewind clone)
    {
        try
        {
            var track = clone?.logic?.getCurrentModelSong();
            if (track == null || !Songs.Values.Any(s => s.Checkpoint?.modelSoundTrack?.Pointer == track.Pointer))
                return null;
            var real = ModelSoundTrackEnum.TOKYO_SKIES;
            ModelSoundTrackEnum.TOKYO_SKIES = track;
            return real;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Checkpoint restore for a package song failed: {e.Message}");
            return null;
        }
    }

    private static void RestoreTokyoSkies(ModelSoundTrack real)
    {
        if (real != null)
            ModelSoundTrackEnum.TOKYO_SKIES = real;
    }

    // The package songs, for unattended test runs (AutoRecorder "packages": true).
    public static List<ModelStoryCheckpoint> ScanForTest()
    {
        Scan();
        return Songs.Values.Select(s => s.Checkpoint).Where(c => c != null).ToList();
    }

    private static void Scan()
    {
        Directory.CreateDirectory(PackagesDir);
        if (!System.IO.File.Exists(Path.Combine(LevelDump.RootDir, "_song-art.json")))
            LevelDump.WriteSongArt();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(PackagesDir, "*.jsab").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var info = ReadHeader(file);
                if (!seen.Add(info.Id))
                {
                    Plugin.Logger.LogWarning($"Package {Path.GetFileName(file)}: id '{info.Id}' is already used by another package; skipped.");
                    continue;
                }
                Register(info);
            }
            catch (Exception e)
            {
                Plugin.Logger.LogWarning($"Package {Path.GetFileName(file)} skipped: {e.Message}");
            }
        }

        // A removed package leaves the list but stays known (ByCheckpoint), in case it's playing.
        foreach (string gone in Songs.Keys.Where(id => !seen.Contains(id)).ToList())
            Songs.Remove(gone);
    }

    // Reads and checks level.json (everything but building the spawns) and the music entry.
    private static Song ReadHeader(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        byte[] bytes = ReadEntry(zip, "level.json", MaxLevelBytes)
            ?? throw new InvalidDataException("no level.json");
        using var doc = JsonDocument.Parse(bytes, Lenient);
        var root = doc.RootElement;

        int format = Int(root, "format", 0);
        if (format != FormatVersion)
            throw new InvalidDataException($"format {format}; this mod reads format {FormatVersion}");

        string id = Str(root, "id");
        if (id == null || !IdPattern.IsMatch(id))
            throw new InvalidDataException("\"id\" must be 1-64 lowercase letters, digits, '.', '_' or '-'");

        var song = new Song
        {
            Id = id,
            File = file,
            Stamp = System.IO.File.GetLastWriteTimeUtc(file),
            Name = Str(root, "name") ?? id,
            Artist = Str(root, "artist") ?? "",
            Author = Str(root, "author") ?? "",
            Difficulty = Math.Clamp(Int(root, "difficulty", 1), 0, 10),
            RemixOf = Str(root, "remixOf"),
        };

        if (song.RemixOf == null)
        {
            var music = zip.GetEntry("music.ogg") ?? throw new InvalidDataException("no music.ogg (or \"remixOf\")");
            if (music.Length > MaxMusicBytes)
                throw new InvalidDataException($"music.ogg is over {MaxMusicBytes >> 20} MB");
        }

        bool any = false;
        foreach (string diff in new[] { "normal", "hard" })
        {
            if (!root.TryGetProperty(diff, out var level))
                continue;
            if (!level.TryGetProperty("spawns", out var spawns) || spawns.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"\"{diff}\" has no \"spawns\" array");
            CheckSpawns(spawns, diff);
            CollectArt(spawns, song.Art);
            any = true;
        }
        if (!any)
            throw new InvalidDataException("no \"normal\" level");
        return song;
    }

    // The game ends a level at its last CheckPoint; without one it would never end.
    private static void CheckSpawns(JsonElement spawns, string diff)
    {
        bool checkpoint = false;
        foreach (var s in spawns.EnumerateArray())
            if (Str(s, "type") == "CheckPoint") { checkpoint = true; break; }
        if (!checkpoint)
            throw new InvalidDataException($"the {diff} level has no CheckPoint (the last one is where it ends)");
    }

    private static void Register(Song info)
    {
        Songs.TryGetValue(info.Id, out var song);
        if (song != null && song.Stamp == info.Stamp && song.File == info.File && song.Checkpoint != null)
            return;

        ModelStoryCheckpoint original = null;
        if (info.RemixOf != null)
        {
            original = FindLevel(info.RemixOf)
                ?? throw new InvalidDataException($"remixOf '{info.RemixOf}': no such song");
        }
        var template = original ?? FirstArcadeSong()
            ?? throw new InvalidOperationException("the game's song list isn't ready");

        var art = new HashSet<string>(info.Art, StringComparer.OrdinalIgnoreCase);
        var originalArt = original?.modelSoundTrack?.flasToLoad;
        for (int i = 0; originalArt != null && i < originalArt.Length; i++)
            if (originalArt[i]?.id != null) art.Add(originalArt[i].id);
        var flas = ResolveArt(art);

        // New songs point their bundle names at nothing (the load is ours); a remix keeps its
        // original's, so the game's preview clip and bundle unload work as for the original.
        string url = original?.modelSoundTrack?.levelEditorFileUrl ?? info.LevelName;
        string mp3 = original?.modelSoundTrack?.mp3Url ?? info.LevelName;

        var album = new ModelSongAlbum("custom_" + info.Id, Label(info), "", template.modelSoundTrack?.modelAlbum?.icon ?? 1);
        album.name = Label(info);
        album.icon = template.modelSoundTrack?.modelAlbum?.icon ?? 1;

        if (song?.Checkpoint != null)
        {
            // Same package, changed file: update the existing song in place so the list,
            // progress and anything holding the song keep working.
            var track = song.Checkpoint.modelSoundTrack;
            track.name = info.Name;
            track.artist = info.Artist;
            track.difficulty = info.Difficulty;
            track.mp3Url = mp3;
            track.levelEditorFileUrl = url;
            track.modelAlbum = album;
            track.flasToLoad = flas;
            song.File = info.File;
            song.Stamp = info.Stamp;
            song.Name = info.Name;
            song.Artist = info.Artist;
            song.Author = info.Author;
            song.Difficulty = info.Difficulty;
            song.RemixOf = info.RemixOf;
            song.Art = info.Art;
            song.Original = original;
            return;
        }

        var soundTrack = new ModelSoundTrack("custom_" + info.Id, info.Difficulty, info.Artist, "", mp3, url, album, flas);
        soundTrack.name = info.Name;
        soundTrack.artist = info.Artist;
        info.Original = original;
        info.Checkpoint = ModelStoryCheckpointEnum.createNoAdd("custom_" + info.Id, template.map, soundTrack);
        Songs[info.Id] = info;
        ByCheckpoint[info.Checkpoint.Pointer] = info;
        Plugin.Logger.LogInfo($"Package song '{info.Name}' ({info.Id}{(original != null ? ", remix of " + info.RemixOf : "")}): art {string.Join(",", art)}");
    }

    private static string Label(Song s) => string.IsNullOrWhiteSpace(s.Author) ? "Custom Level" : s.Author.Trim();

    private static ModelStoryCheckpoint FindLevel(string level)
    {
        if (level.StartsWith("JSBHRD_", StringComparison.OrdinalIgnoreCase))
            level = level.Substring("JSBHRD_".Length);
        var all = ModelStoryCheckpointEnum.ALL;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var song = all[i];
            if (song != null && !song.isStory && !IsCustom(song)
                && string.Equals(song.modelSoundTrack?.levelEditorFileUrl, level, StringComparison.OrdinalIgnoreCase))
                return song;
        }
        return null;
    }

    private static ModelStoryCheckpoint FirstArcadeSong()
    {
        var all = ModelStoryCheckpointEnum.ALL_MULTI;
        return all != null && all.Length > 0 ? all[0] : null;
    }

    // ---- art -------------------------------------------------------------------------------

    // Art bundles a level needs: the ones every vanilla song using each of its hazard types
    // loads (hazard-art.json), plus the bundle of any animation named in a "visual" param.
    // Only song art counts (bundles some vanilla song lists in flasToLoad): the core in-game
    // bundles are always loaded, and since the game unloads a song's flasToLoad when it ends,
    // listing them unloaded art the menus draw with (tk2dBaseSprite.SetSprite NRE every frame).
    private static void CollectArt(JsonElement spawns, HashSet<string> art)
    {
        var table = HazardArt();
        var songArt = SongArt();
        foreach (var s in spawns.EnumerateArray())
        {
            string type = Str(s, "type");
            if (type != null && table.TryGetValue(type, out var bundles))
                art.UnionWith(bundles);
            if (s.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ps.EnumerateObject())
                {
                    if (p.Name.IndexOf("visual", StringComparison.OrdinalIgnoreCase) < 0 || p.Value.ValueKind != JsonValueKind.String)
                        continue;
                    var anim = ModelFlashAnimationEnum.getFromId(p.Value.GetString().Trim());
                    string fla = anim == null || anim.isNull ? null : anim.modelFla?.id;
                    if (!string.IsNullOrEmpty(fla) && songArt.Contains(fla))
                        art.Add(fla);
                }
            }
        }
    }

    // Every bundle some vanilla song loads for itself (the rest are loaded all the time).
    private static HashSet<string> SongArt()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = ModelSoundTrackEnum.ALL;
        for (int i = 0; all != null && i < all.Length; i++)
        {
            var flas = all[i]?.flasToLoad;
            for (int j = 0; flas != null && j < flas.Length; j++)
                if (!string.IsNullOrEmpty(flas[j]?.id)) set.Add(flas[j].id);
        }
        return set;
    }

    private static Dictionary<string, string[]> HazardArt()
    {
        if (_hazardArt != null)
            return _hazardArt;
        _hazardArt = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("hazard-art.json");
            if (stream == null)
                Plugin.Logger.LogWarning("hazard-art.json isn't built into the mod; package songs only load their remix original's art.");
            else
                _hazardArt = JsonSerializer.Deserialize<Dictionary<string, string[]>>(stream) ?? _hazardArt;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Reading hazard-art.json failed: {e.Message}");
        }
        return _hazardArt;
    }

    private static Il2CppReferenceArray<ModelFla> ResolveArt(IEnumerable<string> ids)
    {
        var flas = new List<ModelFla>();
        foreach (string id in ids)
        {
            var fla = ModelFlaEnum.getFromId(id);
            if (fla != null && !fla.isNull)
                flas.Add(fla);
            else
                Plugin.Logger.LogWarning($"Unknown art bundle '{id}'");
        }
        return new Il2CppReferenceArray<ModelFla>(flas.ToArray());
    }

    // ---- loading ---------------------------------------------------------------------------

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicLoadMetaSongAudioOfModelSong), nameof(LogicLoadMetaSongAudioOfModelSong.StartLoading))]
    private static bool StartLoading(LogicLoadMetaSongAudioOfModelSong __instance, Callback __0)
    {
        var song = Find(__instance.modelSong);
        if (song == null)
            return true;
        BeginLoad(song, __0);
        return false;
    }

    // The game's blocking load: only possible once the music is decoded (a remix's always is).
    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicLoadMetaSongAudioOfModelSong), nameof(LogicLoadMetaSongAudioOfModelSong.StartLoadingSync))]
    private static bool StartLoadingSync(LogicLoadMetaSongAudioOfModelSong __instance)
    {
        var song = Find(__instance.modelSong);
        if (song == null)
            return true;
        var callback = __instance.onEndCallback;
        __instance.onEndCallback = null;
        if (song.RemixOf == null && !ClipCurrent(song))
            Plugin.Logger.LogError($"Package song '{song.Name}' was loaded synchronously before its music was decoded; it will play silent.");
        Finish(song, callback);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(LogicLoadMetaSongAudioOfModelSong), nameof(LogicLoadMetaSongAudioOfModelSong.CancelLoading))]
    private static bool CancelLoading(LogicLoadMetaSongAudioOfModelSong __instance)
    {
        var song = Find(__instance.modelSong);
        if (song == null)
            return true;
        if (_pending?.Song == song)
            CancelPending();
        return false;
    }

    private static void BeginLoad(Song song, Callback callback)
    {
        CancelPending();
        if (song.RemixOf != null || ClipCurrent(song))
        {
            Finish(song, callback);
            return;
        }

        try
        {
            byte[] ogg;
            using (var zip = ZipFile.OpenRead(song.File))
                ogg = ReadEntry(zip, "music.ogg", MaxMusicBytes) ?? throw new InvalidDataException("no music.ogg");
            _pending = new PendingLoad { Song = song, Callback = callback, Decode = Task.Run(() => DecodeOgg(ogg)) };
            Plugin.Logger.LogInfo($"Package song '{song.Name}': decoding music");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Package song '{song.Name}': reading music.ogg failed, it will play silent: {e.Message}");
            Finish(song, callback);
        }
    }

    // Worker thread: Ogg Vorbis -> interleaved float samples.
    private static Pcm DecodeOgg(byte[] ogg)
    {
        using var reader = new VorbisReader(new MemoryStream(ogg), true);
        int channels = reader.Channels, rate = reader.SampleRate;
        if (channels < 1 || channels > 8 || rate < 8000 || rate > 192000)
            throw new InvalidDataException($"unsupported audio ({channels} channels, {rate} Hz)");
        long max = (long)MaxMusicSeconds * rate * channels;
        long estimate = Math.Clamp(reader.TotalSamples * channels, channels, max);
        var samples = new float[estimate];
        int count = 0, read;
        var buffer = new float[16384 * channels];
        while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            if (count + read > samples.Length)
            {
                if (count + read > max)
                    throw new InvalidDataException($"music is longer than {MaxMusicSeconds / 60} minutes");
                Array.Resize(ref samples, (int)Math.Min(max, Math.Max(samples.Length * 2L, count + read)));
            }
            Array.Copy(buffer, 0, samples, count, read);
            count += read;
        }
        count -= count % channels;
        if (count == 0)
            throw new InvalidDataException("no audio");
        Array.Resize(ref samples, count);
        return new Pcm { Samples = samples, Channels = channels, Rate = rate };
    }

    // Polled from PracticeRunner.Update until the music is decoded.
    public static void Update()
    {
        var load = _pending;
        if (load == null || !load.Decode.IsCompleted)
            return;
        _pending = null;
        var song = load.Song;
        try
        {
            var pcm = load.Decode.GetAwaiter().GetResult();
            var clip = AudioClip.Create(song.LevelName, pcm.Samples.Length / pcm.Channels, pcm.Channels, pcm.Rate, false);
            // In slices: one 60 MB Il2Cpp copy of a whole song isn't needed.
            int slice = (1 << 20) / pcm.Channels * pcm.Channels;
            for (int offset = 0; offset < pcm.Samples.Length; offset += slice)
            {
                var part = new float[Math.Min(slice, pcm.Samples.Length - offset)];
                Array.Copy(pcm.Samples, offset, part, 0, part.Length);
                clip.SetData(new Il2CppStructArray<float>(part), offset / pcm.Channels);
            }
            clip.hideFlags = HideFlags.DontUnloadUnusedAsset;
            KeepClip(song, clip);
            Plugin.Logger.LogInfo($"Package song '{song.Name}': music decoded ({clip.length:0.0} s, {pcm.Channels} ch, {pcm.Rate} Hz)");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Package song '{song.Name}': music.ogg couldn't be decoded, it will play silent: {e.Message}");
        }
        Finish(song, load.Callback);
    }

    // A decode in flight just finishes on its worker and is dropped.
    private static void CancelPending() => _pending = null;

    // Hands the level and music to the game the way OnMetaSongLoaded/OnSongLoaded do.
    private static void Finish(Song song, Callback callback)
    {
        try
        {
            BuildLevel(song);
            song.Checkpoint.SetMetaSong(song.Meta);
            song.Checkpoint.SetAudioClip(song.RemixOf != null ? OriginalClip(song) : song.Clip);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Package song '{song.Name}' failed to load: {e}");
        }
        callback?.call();
    }

    // Builds (or rebuilds, for hot reload) the song's level from its package.
    private static void BuildLevel(Song song)
    {
        using var zip = ZipFile.OpenRead(song.File);
        byte[] bytes = ReadEntry(zip, "level.json", MaxLevelBytes) ?? throw new InvalidDataException("no level.json");
        using var doc = JsonDocument.Parse(bytes, Lenient);
        var root = doc.RootElement;

        bool hard = MetaGameProgress.instance?.modelGameDifficulty?.isHard() == true;
        string diff = hard && root.TryGetProperty("hard", out _) ? "hard" : "normal";
        var spawns = root.GetProperty(diff).GetProperty("spawns");
        var created = LevelImport.BuildSpawns(spawns, song.LevelName, out int skipped);
        if (created.Count == 0)
            throw new InvalidDataException("no valid spawns");

        if (song.Meta == null || song.Meta.WasCollected)
        {
            song.Meta = new MetaSong(song.Checkpoint);
            song.Meta.hideFlags = HideFlags.DontUnloadUnusedAsset;
            song.Meta.allSpawnsInOrder ??= new Il2CppSystem.Collections.Generic.List<MetaSpawn>();
        }
        song.Meta.name = song.LevelName;
        song.Meta.songName = song.Name;
        song.Meta.artistName = song.Artist;
        song.Meta.modelSong = song.Checkpoint;
        LevelImport.ReplaceSpawns(song.Meta, created);
        LevelImport.SetSignature(song.LevelName, bytes);
        Plugin.Logger.LogInfo($"Package song '{song.Name}' ({diff}): {created.Count} spawns ({skipped} skipped)");
    }

    // Hot reload (F5, editor preview): re-read the package and rebuild the level in place.
    public static bool Reload(ModelStoryCheckpoint checkpoint)
    {
        var song = Find(checkpoint);
        if (song == null)
            return false;
        try
        {
            if (System.IO.File.Exists(song.File))
                Register(ReadHeader(song.File));
            BuildLevel(song);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError($"Package song '{song.Name}' failed to reload, keeping the current level: {e.Message}");
            return false;
        }
    }

    private static bool ClipCurrent(Song song) => song.Clip != null && !song.Clip.WasCollected && song.ClipStamp == song.Stamp;

    // Decoded songs stay in memory (about 10 MB a minute); only the last few are kept.
    private static void KeepClip(Song song, AudioClip clip)
    {
        if (song.Clip != null && !song.Clip.WasCollected && song.Clip.Pointer != clip.Pointer)
            UnityEngine.Object.Destroy(song.Clip);
        song.Clip = clip;
        song.ClipStamp = song.Stamp;
        LoadedClips.Remove(song);
        LoadedClips.Add(song);
        while (LoadedClips.Count > ClipsKept)
        {
            var old = LoadedClips[0];
            LoadedClips.RemoveAt(0);
            if (old.Clip == null || old.Clip.WasCollected)
                continue;
            if (old.Checkpoint != null && old.Checkpoint.audioClip?.Pointer == old.Clip.Pointer)
                old.Checkpoint.SetAudioClip(null);
            UnityEngine.Object.Destroy(old.Clip);
            old.Clip = null;
        }
    }

    // A remix plays its original's music, read from the original's bundle like the game does.
    private static AudioClip OriginalClip(Song song)
    {
        var track = song.Original?.modelSoundTrack;
        if (track == null)
            return null;
        string bundle = song.Original.GetAssetBundlePath();
        AssetManager.LoadAssetBundleSync(bundle);
        var obj = AssetManager.LoadAssetFromBundleOrResourceSync(bundle, track.mp3Url);
        var clip = obj?.TryCast<AudioClip>();
        if (clip == null)
            Plugin.Logger.LogError($"Package song '{song.Name}': couldn't read {song.RemixOf}'s music.");
        return clip;
    }

    // ---- progress ----------------------------------------------------------------------

    // The game keeps a progress entry per song it knew at startup and returns a NULL one for
    // anything else; package songs get their own (in memory for now), and are never locked.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MetaArcadeGroupSongProgress), nameof(MetaArcadeGroupSongProgress.getSongProgressFromOnModel))]
    private static bool SongProgress(MetaArcadeGroupSongProgress __instance, ModelStoryCheckpoint __0, ref MetaArcadeSongProgress __result)
    {
        var song = Find(__0);
        if (song == null)
            return true;
        string key = (__instance.modelDiff?.id ?? "") + "|" + song.Id;
        if (!Progress.TryGetValue(key, out var progress) || progress.WasCollected)
            Progress[key] = progress = new MetaArcadeSongProgress(__0);
        __result = progress;
        return false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MetaArcadeSongProgress), nameof(MetaArcadeSongProgress.isUnlocked))]
    private static void IsUnlocked(MetaArcadeSongProgress __instance, ref bool __result)
    {
        if (!__result && IsCustom(__instance.modelSong))
            __result = true;
    }

    // ---- helpers -----------------------------------------------------------------------

    private static byte[] ReadEntry(ZipArchive zip, string name, long max)
    {
        var entry = zip.GetEntry(name);
        if (entry == null)
            return null;
        if (entry.Length > max)
            throw new InvalidDataException($"{name} is over {max >> 20} MB");
        using var stream = entry.Open();
        using var buffer = new MemoryStream((int)entry.Length);
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static int Int(JsonElement obj, string key, int fallback) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)
            ? i
            : fallback;
}
