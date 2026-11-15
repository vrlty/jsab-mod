"""Build JsabPractice/hazard-art.json: which art bundles each hazard type needs.

The game loads a song's art bundles (ModelSoundTrack.flasToLoad) before playing it, so a
hazard's art is in every song that uses it. A hazard type therefore needs the bundles that
ALL vanilla songs using it load (the intersection). Package songs (CustomSongs.cs) load the
union of that over the hazard types they use.

Inputs, from the levels folder (Documents/JSAB Levels by default):
  _song-art.json   { "MUSIC_FOX": ["jsb_...", ...] }  written by the mod when a level loads
  vanilla/*.json   the game's levels (tools/extract_levels.py)

usage: python hazard_art.py [--levels DIR]
"""
import argparse
import glob
import json
import os

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "..", "JsabPractice", "hazard-art.json")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--levels", default=os.path.join(os.path.expanduser("~"), "Documents", "JSAB Levels"))
    args = ap.parse_args()

    with open(os.path.join(args.levels, "_song-art.json"), encoding="utf-8") as f:
        song_art = {k.upper(): set(v) for k, v in json.load(f).items()}

    needs = {}      # type -> intersection of song art
    missing = set()
    for path in sorted(glob.glob(os.path.join(args.levels, "vanilla", "*.json"))):
        level = os.path.splitext(os.path.basename(path))[0].upper()
        song = level[len("JSBHRD_"):] if level.startswith("JSBHRD_") else level
        art = song_art.get(song)
        if art is None:
            missing.add(level)
            continue
        with open(path, encoding="utf-8") as f:
            types = {s.get("type") for s in json.load(f).get("spawns", [])}
        for t in types - {None}:
            needs[t] = needs[t] & art if t in needs else set(art)

    table = {t: sorted(a) for t, a in sorted(needs.items())}
    with open(OUT, "w", encoding="utf-8", newline="\n") as f:
        json.dump(table, f, indent=1)
        f.write("\n")
    print(f"{len(table)} hazard types -> {os.path.normpath(OUT)}")
    if missing:
        print(f"no song art for {len(missing)} levels (story/ambience?): {', '.join(sorted(missing)[:10])}")


if __name__ == "__main__":
    main()
