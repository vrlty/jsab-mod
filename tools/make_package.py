"""Pack a level into a .jsab package (the mod reads <levels folder>/packages/*.jsab).

  python make_package.py --id my-level --name "Song" --artist "Musician" --author "Me" \
      --normal level.json [--hard hard.json] (--music song.ogg | --remix-of MUSIC_FOX) \
      [--difficulty 2] [--out my-level.jsab]

--normal/--hard take level files in the editor's format (a "spawns" array, like
custom/<LEVEL>.json). The level ends at its last CheckPoint.
"""
import argparse
import json
import os
import zipfile

FORMAT = 1  # CustomSongs.FormatVersion


def spawns_of(path):
    with open(path, encoding="utf-8") as f:
        spawns = json.load(f).get("spawns")
    if not isinstance(spawns, list):
        raise SystemExit(f"{path}: no \"spawns\" array")
    if not any(s.get("type") == "CheckPoint" for s in spawns):
        raise SystemExit(f"{path}: no CheckPoint (the last one is where the level ends)")
    return spawns


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--id", required=True)
    ap.add_argument("--name", required=True)
    ap.add_argument("--artist", default="")
    ap.add_argument("--author", default="")
    ap.add_argument("--difficulty", type=int, default=1)
    ap.add_argument("--normal", required=True)
    ap.add_argument("--hard")
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--music")
    src.add_argument("--remix-of")
    ap.add_argument("--out")
    args = ap.parse_args()

    level = {"format": FORMAT, "id": args.id, "name": args.name, "artist": args.artist,
             "author": args.author, "difficulty": args.difficulty}
    if args.remix_of:
        level["remixOf"] = args.remix_of
    level["normal"] = {"spawns": spawns_of(args.normal)}
    if args.hard:
        level["hard"] = {"spawns": spawns_of(args.hard)}

    out = args.out or args.id + ".jsab"
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("level.json", json.dumps(level, indent=1))
        if args.music:
            z.write(args.music, "music.ogg", compress_type=zipfile.ZIP_STORED)  # already compressed
    print(f"wrote {out}")


if __name__ == "__main__":
    main()
