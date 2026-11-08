"""Export every JSAB level (normal and hardcore) and its music straight from the game files.

Each bundle in <game>/JSB_Data/StreamingAssets/AssetBundles/levels holds a song's levels
(MetaSong: MUSIC_FOX, JSBHRD_MUSIC_FOX, ...) and its music (AudioClip). This writes:
  vanilla/<LEVEL>.json   the level, in the same format the mod dumps when a level is played
                         (LevelDump.cs), so the editor and custom levels work the same
  music/<clip>.ogg       the song (Ogg Vorbis)
  music/index.json       { "LEVEL": "<clip>.ogg", ... }

Hazard type names come from the mod's _hazard-catalog.json (written the first time the game
runs with the mod), which maps the levels' type numbers to names.

usage: python extract_levels.py [--game DIR] [--out DIR] [--only SUBSTR ...]
                                [--overwrite] [--no-music] [--compare]
  --overwrite  replace level files that already exist (default: keep the mod's dumps)
  --compare    write nothing; compare against the existing vanilla/ dumps and report
"""
import argparse
import io
import json
import os
import struct
import sys

import UnityPy

sys.path.insert(0, os.path.dirname(__file__))
from extract_art import documents_dir, find_game  # noqa: E402

LEVELS_SUBDIR = os.path.join("JSB_Data", "StreamingAssets", "AssetBundles", "levels")
LEVEL_FORMAT = 3  # LevelDump.FormatVersion (3: spawns carry randomOffset)


def num(v):
    """A float32 written as .NET would: shortest form that round-trips, ints without '.0'."""
    v = float(v)
    if v == int(v) and abs(v) < 1e15:
        return int(v)
    for p in range(1, 10):
        s = f"{v:.{p}g}"
        if struct.unpack("f", struct.pack("f", float(s)))[0] == struct.unpack("f", struct.pack("f", v))[0]:
            return float(s)
    return v


def file_name(level):
    for c in '<>:"/\\|?*':
        level = level.replace(c, "_")
    return level + ".json"


def export_spawn(sp, types):
    mp = sp["metaParams"]
    tl = mp["metaTimeline"]
    pos = mp["metaPosition"]
    params = {}
    for p in mp["metaParams"]:
        key = p["key"] or f"#{p['keyAsInt']}"
        if key in ("timeline", "pos"):   # exported as their own fields
            continue
        params[key] = p["metaValue"]["value"]
    return {
        "type": types.get(sp["modelSpawnIndex"], f"#{sp['modelSpawnIndex']}"),
        "layer": 0,
        "timeMs": tl["timeInMs"]["value"],
        "warningMs": tl["warningTime"]["value"],
        "outroMs": tl["outroTime"]["value"],
        "unique": f"unique{tl['modelUniqueId'] + 1}",
        "timelineName": tl["name"],
        "randomOffset": sp["randomOffset"],
        "pos": {
            "x": pos["xRelativeToScreen"]["value"],
            "y": pos["yRelativeToScreen"]["value"],
            "xUsed": bool(pos["xUsed"]),
            "yUsed": bool(pos["yUsed"]),
            "xRandomMargin": num(pos["xRandomMargin"]),
            "yRandomMargin": num(pos["yRandomMargin"]),
        },
        "params": dict(sorted(params.items())),
    }


def export_level(song, types):
    return {
        "format": LEVEL_FORMAT,
        "name": song["m_Name"],
        "song": song["songName"],
        "songName": song["songName"],
        "artist": song["artistName"],
        "spawns": [export_spawn(sp, types) for sp in song["allSpawnsInOrder"]],
    }


def encode_ogg(wav_bytes):
    import soundfile as sf
    data, rate = sf.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=True)
    buf = io.BytesIO()
    # Whole-song writes overflow libsndfile's stack in the Vorbis encoder; write in blocks.
    with sf.SoundFile(buf, "w", samplerate=rate, channels=data.shape[1], format="OGG", subtype="VORBIS") as f:
        for i in range(0, len(data), 32768):
            f.write(data[i:i + 32768])
    return buf.getvalue()


def compare(mine, theirs):
    """Differences between two level dicts, as short strings."""
    out = []
    for k in ("name", "songName", "artist"):
        if mine.get(k) != theirs.get(k):
            out.append(f"{k}: {mine.get(k)!r} vs {theirs.get(k)!r}")
    a, b = mine["spawns"], theirs["spawns"]
    if len(a) != len(b):
        out.append(f"spawn count {len(a)} vs {len(b)}")
    for i, (x, y) in enumerate(zip(a, b)):
        if x != y:
            keys = sorted(k for k in set(x) | set(y) if x.get(k) != y.get(k))
            out.append(f"spawn {i}: " + "; ".join(f"{k}: {x.get(k)!r} vs {y.get(k)!r}" for k in keys))
            if len(out) > 8:
                break
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game")
    ap.add_argument("--out", help="the levels folder (default: Documents/JSAB Levels)")
    ap.add_argument("--only", nargs="*", default=[])
    ap.add_argument("--overwrite", action="store_true")
    ap.add_argument("--no-music", action="store_true")
    ap.add_argument("--compare", action="store_true")
    args = ap.parse_args()

    game = args.game or find_game()
    if not game:
        sys.exit("Couldn't find Just Shapes & Beats; pass --game")
    src = os.path.join(game, LEVELS_SUBDIR)
    out = args.out or os.path.join(documents_dir(), "JSAB Levels")
    catalog_path = os.path.join(out, "_hazard-catalog.json")
    if not os.path.exists(catalog_path):
        sys.exit(f"No {catalog_path}: start the game once with the mod (it writes the hazard catalog)")
    with open(catalog_path, encoding="utf-8") as f:
        catalog = json.load(f)
    hazards = catalog if isinstance(catalog, list) else catalog.get("hazards", [])
    types = {h["index"]: h["id"] for h in hazards}

    vanilla = os.path.join(out, "vanilla")
    music = os.path.join(out, "music")
    os.makedirs(vanilla, exist_ok=True)
    os.makedirs(music, exist_ok=True)
    index_path = os.path.join(music, "index.json")
    index = {}
    if os.path.exists(index_path):
        with open(index_path, encoding="utf-8") as f:
            index = json.load(f)

    bundles = sorted(f for f in os.listdir(src) if not f.endswith(".manifest"))
    if args.only:
        bundles = [b for b in bundles if any(s.lower() in b.lower() for s in args.only)]

    written = kept = same = differ = songs = 0
    for n, bundle in enumerate(bundles, 1):
        env = UnityPy.load(os.path.join(src, bundle))
        levels, clips = [], []
        for obj in env.objects:
            if obj.type.name == "MonoBehaviour":
                tree = obj.read_typetree()
                if "allSpawnsInOrder" in tree:
                    levels.append(tree)
            elif obj.type.name == "AudioClip":
                clips.append(obj)
        names = []
        for song in levels:
            level = export_level(song, types)
            path = os.path.join(vanilla, file_name(level["name"]))
            names.append(level["name"])
            if args.compare:
                if os.path.exists(path):
                    with open(path, encoding="utf-8") as f:
                        diffs = compare(level, json.load(f))
                    if diffs:
                        differ += 1
                        print(f"  {level['name']}: differs from the mod's dump\n    " + "\n    ".join(diffs))
                    else:
                        same += 1
                continue
            if os.path.exists(path) and not args.overwrite:
                kept += 1
                continue
            with open(path, "w", encoding="utf-8") as f:
                json.dump(level, f, indent=2, ensure_ascii=False)
            written += 1

        if not args.compare and not args.no_music and clips:
            clip = clips[0].read()
            ogg_name = clip.m_Name + ".ogg"
            ogg_path = os.path.join(music, ogg_name)
            if not os.path.exists(ogg_path) or args.overwrite:
                wav = next(iter(clip.samples.values()))
                with open(ogg_path, "wb") as f:
                    f.write(encode_ogg(wav))
                songs += 1
            for name in names:
                index[name] = ogg_name
        print(f"[{n}/{len(bundles)}] {bundle}: {', '.join(names) or 'no levels'}" + (f" + {len(clips)} clip(s)" if clips else ""))

    if args.compare:
        print(f"\n{same} levels identical to the mod's dumps, {differ} differ")
        return
    with open(index_path, "w", encoding="utf-8") as f:
        json.dump(dict(sorted(index.items())), f, indent=1)
    print(f"\n{written} levels written, {kept} kept (already there; --overwrite replaces them), {songs} songs encoded")
    print(f"-> {vanilla}\n-> {music}")


if __name__ == "__main__":
    main()
