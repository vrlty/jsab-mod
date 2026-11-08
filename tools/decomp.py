"""Read the game's code: decompiled, typed, one file per class.

  python tools/decomp.py --setup          # once, game running (~25 min, in the background)
  python tools/decomp.py --usages         # then once, game running: names statics/strings
  python tools/decomp.py RoundSpikes Wall # -> tools/out/decomp/RoundSpikes.c, Wall.c
  python tools/decomp.py --apply-layout   # after a new _il2cpp-layout.json

A persistent Ghidra project (~/ghidra-projects/jsab) holds GameAssembly.dll analysed
once and typed from the game's own IL2CPP layout (the mod's GET /il2cpp/layout writes
<levels folder>/_il2cpp-layout.json; tools/ghidra/ApplyLayout.java applies it): classes
are structs with their real field names, methods are named Class$$method with their
signatures. Exports are cached; the IL2CPP boilerplate (class-init checks, GC write
barriers) is stripped so a class reads in a few thousand tokens.
"""
import argparse
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
GHIDRA = os.environ.get("GHIDRA_INSTALL_DIR", r"C:\ghidra\ghidra_12.1.4_PUBLIC")
HEADLESS = os.path.join(GHIDRA, "support", "analyzeHeadless.bat")
PROJECT_DIR = os.path.join(os.path.expanduser("~"), "ghidra-projects", "jsab")
PROJECT = "JSAB"
GAME_DLL = r"C:\Program Files (x86)\Steam\steamapps\common\Just Shapes & Beats\GameAssembly.dll"
LAYOUT = os.path.join(os.path.expanduser("~"), "Documents", "JSAB Levels", "_il2cpp-layout.json")
OUT = os.path.join(HERE, "out", "decomp")
SCRIPTS = os.path.join(HERE, "ghidra")


def short_dir(path):
    """The folder's 8.3 name: analyzeHeadless.bat re-expands its arguments through cmd,
    which breaks on the "(x86)" and "&" in the game's path. The file keeps its own name,
    so the program in the project is still GameAssembly.dll."""
    import ctypes
    folder, name = os.path.split(path)
    buf = ctypes.create_unicode_buffer(1024)
    n = ctypes.windll.kernel32.GetShortPathNameW(folder, buf, len(buf))
    return os.path.join(buf.value if n else folder, name)


def headless(*args, log=None):
    env = dict(os.environ, GHIDRA_HEADLESS_MAXMEM=os.environ.get("GHIDRA_HEADLESS_MAXMEM", "16G"))
    cmd = [HEADLESS, PROJECT_DIR, PROJECT, *args]
    with open(log, "w", encoding="utf-8") if log else open(os.devnull, "w") as f:
        p = subprocess.run(cmd, env=env, stdout=f if log else subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    return p


BRIDGE = "http://127.0.0.1:47321"
USAGE_SLOTS = os.path.join(os.path.dirname(LAYOUT), "_il2cpp-usage-slots.json")
USAGES = os.path.join(os.path.dirname(LAYOUT), "_il2cpp-usages.json")


def bridge(path, timeout=300):
    """GET from the mod's editor bridge; None when the game isn't running."""
    import json
    import urllib.request
    try:
        with urllib.request.urlopen(BRIDGE + path, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except OSError:
        return None


def setup():
    os.makedirs(PROJECT_DIR, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    got = bridge("/il2cpp/layout")
    if got:
        print(f"layout from the game: {got.get('classes')} classes")
    elif not os.path.exists(LAYOUT):
        sys.exit(f"no {LAYOUT}: start the game with the mod (its bridge writes it) and run again")
    log = os.path.join(OUT, "_setup.log")
    print(f"importing + analysing (long); log: {log}")
    # Layout first (functions, names, types), then the full analysis with them in place.
    p = headless("-import", short_dir(GAME_DLL), "-overwrite", "-scriptPath", SCRIPTS,
                 "-preScript", "ApplyLayout.java", LAYOUT, log=log)
    print("done" if p.returncode == 0 else f"failed ({p.returncode}); see {log}")


def usages():
    """Name the metadata-usage slots (X_TypeInfo->static_fields->F, string literals, ...):
    list them in the project, have the running game resolve them, apply."""
    log = os.path.join(OUT, "_usages.log")
    p = headless("-process", "GameAssembly.dll", "-noanalysis", "-readOnly", "-scriptPath", SCRIPTS,
                 "-postScript", "ListUsageSlots.java", USAGE_SLOTS, log=log)
    if p.returncode != 0:
        sys.exit(f"listing slots failed; see {log}")
    got = bridge("/il2cpp/usages")
    if not got or not got.get("ok"):
        sys.exit(f"the game has to be running with the mod to resolve the slots ({got})")
    print(f"resolved by the game: {got.get('resolved')} ({got.get('failed')} not)")
    p = headless("-process", "GameAssembly.dll", "-noanalysis", "-scriptPath", SCRIPTS,
                 "-postScript", "ApplyUsages.java", USAGES, log=log)
    drop_cache()
    print("done" if p.returncode == 0 else f"failed ({p.returncode}); see {log}")


def apply_layout():
    log = os.path.join(OUT, "_layout.log")
    p = headless("-process", "GameAssembly.dll", "-noanalysis", "-scriptPath", SCRIPTS,
                 "-postScript", "ApplyLayout.java", LAYOUT, log=log)
    drop_cache()
    print("done" if p.returncode == 0 else f"failed ({p.returncode}); see {log}")


# IL2CPP boilerplate the decompiler shows in every method.
CLEAN = [
    # metadata init on a method's first run:
    #   if (flag == '\0') { il2cpp_codegen_initialize_runtime_metadata(&X_TypeInfo); ... flag = '\x01'; }
    (re.compile(r"[ \t]*if \((\w+) == '\\0'\) \{\n(?:[^\n]*\n)*?[ \t]*\1 = '\\x01';\n[ \t]*\}\n"), ""),
    # class-init check before touching statics:
    #   if (((X_TypeInfo->field_0x133 & 4) != 0) && (*(int *)&X_TypeInfo->field_0xe0 == 0)) il2cpp_runtime_class_init(X);
    (re.compile(r"[ \t]*if \(\(\([^;{}]*?0x133[^;{}]*?0xe0[^;{}]*?\)\s*\{\s*(?:il2cpp_runtime_class_init|FUN_180229200)\s*\([^;{}]*?\);\s*\}\n"), ""),
    # GC write barrier after a reference store
    # (card marking: opens with "uVarN = (uint)(... >> 0xc);" and loops on a LOCK'd compare;
    # anchored on that so a real "if (bVarN) {...}" block is kept)
    (re.compile(r"[ \t]*if \((?:bVar\d+|iVar\d+ != 0)\) \{\n[ \t]*uVar\d+ = \(uint\)\([^\n]*>> 0xc\);\n(?:[^\n]*\n){0,14}?[ \t]*\} while \([^\n]*\);\n[ \t]*\}\n"), ""),
    (re.compile(r"[ \t]*bVar\d+ = DAT_1822257d8 != 0;\n"), ""),
    # the trap after a throw helper, and Ghidra's overlap warning
    (re.compile(r"[ \t]*pcVar\d+ = \(code \*\)swi\(3\);\n[ \t]*\(\*pcVar\d+\)\(\);\n"), ""),
    (re.compile(r"[ \t]*/\* WARNING: (?:Globals starting with '_' overlap|Subroutine does not return)[^\n]*\*/\n"), ""),
]


def clean(text):
    text = text.replace("\r", "")
    for rx, rep in CLEAN:
        text = rx.sub(rep, text)
    text = text.replace("FUN_180260a50()", "ThrowNullReference()").replace("FUN_1814c7fe0(0)", "ThrowIndexOutOfRange()")
    # The game's Flash-engine namespaces: Assets_Plugins_lachhh_lachhhEngine_animation_X -> X
    text = re.sub(r"\bAssets_Plugins_(?:[a-z][A-Za-z0-9]*_)+", "", text)
    return re.sub(r"\n{3,}", "\n\n", text)


def drop_cache():
    """Exports are stale once names or types change."""
    if os.path.isdir(OUT):
        for f in os.listdir(OUT):
            if f.endswith(".c"):
                os.remove(os.path.join(OUT, f))


def export(classes, force=False):
    os.makedirs(OUT, exist_ok=True)
    if not os.path.isdir(os.path.join(PROJECT_DIR, PROJECT + ".rep")):
        sys.exit("no Ghidra project yet: run  python tools/decomp.py --setup")
    need = [c for c in classes if force or not os.path.exists(os.path.join(OUT, c.replace("/", "$") + ".c"))]
    if need:
        raw = os.path.join(OUT, "_raw")
        p = headless("-process", "GameAssembly.dll", "-noanalysis", "-readOnly", "-scriptPath", SCRIPTS,
                     "-postScript", "ExportClasses.java", raw, *need, log=os.path.join(OUT, "_export.log"))
        if p.returncode != 0:
            sys.exit(f"export failed; see {os.path.join(OUT, '_export.log')}")
        for c in need:
            name = c.replace("/", "$") + ".c"
            with open(os.path.join(raw, name), encoding="utf-8") as f:
                text = clean(f.read())
            with open(os.path.join(OUT, name), "w", encoding="utf-8") as f:
                f.write(text)
    for c in classes:
        path = os.path.join(OUT, c.replace("/", "$") + ".c")
        print(f"{path}  ({os.path.getsize(path) // 1024} KB)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("classes", nargs="*")
    ap.add_argument("--setup", action="store_true")
    ap.add_argument("--apply-layout", action="store_true")
    ap.add_argument("--usages", action="store_true", help="name static/string/method slots (game must be running)")
    ap.add_argument("--force", action="store_true", help="re-export even if cached")
    a = ap.parse_args()
    if a.setup:
        setup()
    elif a.apply_layout:
        apply_layout()
    elif a.usages:
        usages()
    elif a.classes:
        export(a.classes, a.force)
    else:
        ap.print_help()


if __name__ == "__main__":
    main()
