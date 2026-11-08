"""Export JSAB's hazard/FX art straight from the game's asset bundles.

Reads every bundle in <game>/JSB_Data/StreamingAssets/AssetBundles/modelfla.
Each one holds a Flash document converted for Unity:
  * a texture atlas (Texture2D),
  * a tk2dSpriteCollectionData saying where each sprite sits in the atlas,
  * a ModelFlaAsset with every symbol's timeline: per frame, a list of display
    objects (an atlas sprite or a nested symbol) with an affine matrix and a
    tint/alpha colour transform.

Output (default: Documents/JSAB Levels/art):
  index.json        every document and its symbols with frame counts
  <doc>.png         the atlas
  <doc>.json.gz     sprites + symbol timelines, gzipped JSON in the format below
                    (Flash timelines repeat a lot; gzip shrinks them ~17x)

<doc>.json.gz:
  {"doc": name, "atlas": "<doc>.png" | null, "size": [w, h],
   "sprites": [[x, y, w, h, rot], ...],     # atlas pixels, top-left origin;
                                             # rot = clockwise degrees (0/90/-90)
                                             # that turn the atlas pixels upright
   "symbols": {NAME: {"frames": [[obj, ...], ...],
                      "stops": [frame, ...],     # clip stops on entering these
                      "labels": {label: frame}}}}
  obj (all keys optional except one of s/c/text):
    "s": sprite index, "r": [x, y, w, h] where the sprite lands in local units
    "c": nested symbol name, "i": instance id (same id across frames = same
         instance, so its own timeline keeps running)
    "text": {...} text field info
    "m": [a, b, c, d, tx, ty]   local->parent, Flash convention, y down
                                (x' = a*x + c*y + tx, y' = b*x + d*y + ty);
                                omitted = identity
    "t": [r, g, b, amount]      tint, colours 0..1: rgb' = rgb*(1-amount) + tint*amount
    "a": alpha multiplier       omitted = 1

usage: python extract_art.py [--game DIR] [--out DIR] [--only SUBSTR ...]
"""
import argparse
import ctypes
import gzip
import datetime
import json
import os
import re
import sys

import UnityPy

STEAM_APP_DIR = "Just Shapes & Beats"
BUNDLE_SUBDIR = os.path.join("JSB_Data", "StreamingAssets", "AssetBundles", "modelfla")
FORMAT_VERSION = 1


# ---------------------------------------------------------------- locations

def documents_dir():
    """Same folder the plugin uses (Environment.SpecialFolder.MyDocuments)."""
    try:
        buf = ctypes.create_unicode_buffer(260)
        if ctypes.windll.shell32.SHGetFolderPathW(None, 5, None, 0, buf) == 0:
            return buf.value
    except (AttributeError, OSError):
        pass
    return os.path.join(os.path.expanduser("~"), "Documents")


def steam_libraries():
    roots = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            roots.append(winreg.QueryValueEx(k, "SteamPath")[0])
    except OSError:
        pass
    roots.append(r"C:\Program Files (x86)\Steam")
    libs = []
    for root in roots:
        libs.append(root)
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        if os.path.exists(vdf):
            with open(vdf, encoding="utf-8", errors="replace") as f:
                libs += [p.replace("\\\\", "\\") for p in re.findall(r'"path"\s+"([^"]+)"', f.read())]
    return libs


def find_game():
    for lib in steam_libraries():
        game = os.path.join(lib, "steamapps", "common", STEAM_APP_DIR)
        if os.path.isdir(os.path.join(game, BUNDLE_SUBDIR)):
            return game
    return None


# ---------------------------------------------------------------- reading

def read_bundle(path):
    env = UnityPy.load(path)
    fla = coll = atlas = None
    for obj in env.objects:
        kind = obj.type.name
        if kind == "MonoBehaviour":
            tree = obj.read_typetree()
            if "allSymbols" in tree:
                fla = tree
            elif "spriteDefinitions" in tree:
                coll = tree
        elif kind == "Texture2D":
            atlas = obj.read().image.convert("RGBA")
    return fla, coll, atlas


def sprite_entry(d, w, h):
    """Atlas rect (pixels, top-left origin) + rotation needed to stand it upright.

    tk2d corners are BL, BR, TL, TR. Where the sprite's local +x axis
    (BL -> BR) points in the atlas tells us how it was packed.
    """
    us = [uv["x"] for uv in d["uvs"]]
    vs = [uv["y"] for uv in d["uvs"]]
    x0, x1 = round(min(us) * w), round(max(us) * w)
    y0, y1 = round((1 - max(vs)) * h), round((1 - min(vs)) * h)
    du = d["uvs"][1]["x"] - d["uvs"][0]["x"]
    dv = -(d["uvs"][1]["y"] - d["uvs"][0]["y"])  # atlas image y runs down
    if abs(du) >= abs(dv):
        rot = 0
    elif dv < 0:
        rot = 90    # local right points up the atlas: turn clockwise
    else:
        rot = -90   # local right points down the atlas: turn counter-clockwise
    return [x0, y0, x1 - x0, y1 - y0, rot]


def r4(v):
    v = round(float(v), 4)
    return 0.0 if v == 0 else v  # no "-0.0"


def convert_object(od, instance_ids):
    out = {}
    shape = od["shapeObjectInfo"]
    text = od["textFieldInfo"]
    if shape["idInSpriteCollection"] >= 0:
        out["s"] = shape["idInSpriteCollection"]
        out["r"] = [r4(shape["anchorX"]), r4(shape["anchorY"]), r4(od["width"]), r4(od["height"])]
    elif od["theClassName"] == "TextField" or text["text"]:
        out["text"] = {"text": text["text"], "size": text["textSize"], "font": text["fontName"],
                       "align": text["textAlign"], "id": text["textId"],
                       "rgb": [r4(text["r"]), r4(text["g"]), r4(text["b"])],
                       "w": r4(od["width"]), "h": r4(od["height"])}
    else:
        out["c"] = od["theClassName"]
        out["i"] = instance_ids.setdefault(od["name"], len(instance_ids))

    m = od["transform"]["matrix"]
    mat = [r4(m[k]) for k in ("a", "b", "c", "d", "tx", "ty")]
    if mat != [1, 0, 0, 1, 0, 0]:
        out["m"] = mat
    ct = od["transform"]["colorTransform"]
    if ct["tintPrct"] > 0:
        out["t"] = [r4(ct["flashTintR"]), r4(ct["flashTintG"]), r4(ct["flashTintB"]), r4(ct["tintPrct"])]
    if ct["alpha"] != 1:
        out["a"] = r4(ct["alpha"])
    return out


def convert_symbol(sym):
    instance_ids = {}
    frames = [[convert_object(od, instance_ids) for od in fr["metaDisplayObjects"]]
              for fr in sym["metaMovieClipFrames"]]
    out = {"frames": frames}
    stops = [i for i, s in enumerate(sym["stopOnEnter"]) if s]
    if stops:
        out["stops"] = stops
    if sym["frameLabels"]:
        out["labels"] = dict(zip(sym["frameLabels"], sym["frameLabelsId"]))
    return out


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--game", help="game install folder (default: found via Steam)")
    ap.add_argument("--out", help="output folder (default: Documents/JSAB Levels/art)")
    ap.add_argument("--only", nargs="*", default=[], help="only bundles whose name contains one of these")
    args = ap.parse_args()

    game = args.game or find_game()
    if not game:
        sys.exit("Couldn't find Just Shapes & Beats; pass --game")
    src = os.path.join(game, BUNDLE_SUBDIR)
    out = args.out or os.path.join(documents_dir(), "JSAB Levels", "art")
    os.makedirs(out, exist_ok=True)

    names = sorted(f for f in os.listdir(src) if not f.endswith(".manifest"))
    if args.only:
        names = [n for n in names if any(s in n for s in args.only)]

    index = {"version": FORMAT_VERSION, "generated": datetime.datetime.now().isoformat(timespec="seconds"),
             "source": src, "docs": {}}
    skipped = []
    for n, name in enumerate(names, 1):
        fla, coll, atlas = read_bundle(os.path.join(src, name))
        if fla is None:
            skipped.append(name)
            continue
        doc = {"doc": fla["docName"], "atlas": None, "size": None, "sprites": [], "symbols": {}}
        if atlas is not None and coll is not None:
            doc["atlas"] = f"{name}.png"
            doc["size"] = list(atlas.size)
            doc["sprites"] = [sprite_entry(d, *atlas.size) for d in coll["spriteDefinitions"]]
            atlas.save(os.path.join(out, doc["atlas"]))
        for sym in fla["allSymbols"]:
            doc["symbols"][sym["theClassName"]] = convert_symbol(sym)
        data = json.dumps(doc, separators=(",", ":")).encode("utf-8")
        with open(os.path.join(out, f"{name}.json.gz"), "wb") as f:
            f.write(gzip.compress(data, mtime=0))
        stale = os.path.join(out, f"{name}.json")  # format before gzip
        if os.path.exists(stale):
            os.remove(stale)
        index["docs"][name] = {
            "doc": doc["doc"], "data": f"{name}.json.gz", "atlas": doc["atlas"],
            "symbols": {k: len(v["frames"]) for k, v in doc["symbols"].items()},
        }
        print(f"[{n}/{len(names)}] {name}: {len(doc['symbols'])} symbols, {len(doc['sprites'])} sprites"
              + ("" if doc["atlas"] else " (no atlas)"))

    with open(os.path.join(out, "index.json"), "w", encoding="utf-8") as f:
        json.dump(index, f, indent=1)
    print(f"\n{len(index['docs'])} documents -> {out}")
    if skipped:
        print(f"skipped (no animation data): {', '.join(skipped)}")


if __name__ == "__main__":
    main()
