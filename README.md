# JSAB Mod

A mod for **Just Shapes & Beats** (Steam, Windows) with a browser level editor:

- **Custom songs.** Play `.jsab` song packages (your own music or a remix of a game song, normal and hardcore) at the end of the Practice list.
- **Level editor.** A single HTML page (Chrome or Edge): timeline with waveform and beat grid, a stage preview that simulates hazards from the game's own code, preview in the running game, song packages to share.
- **Practice tools.** No-hit auto-restart, hitbox overlay, freeze on hit, hot-reload of edited levels.

Not affiliated with Berzerk Studio. You need your own copy of the game.

## Install (players)

Download `jsab-mod-<version>.zip` from [Releases](https://github.com/vrlty/jsab-mod/releases) and extract it into the game folder (Steam: right-click the game > Manage > Browse local files), next to `JSB.exe`. It includes the mod loader ([BepInEx](https://github.com/BepInEx/BepInEx) 6.0.0-be.791), the mod and `JSAB Level Editor.html`. The first launch needs an internet connection and takes a minute or two.

The README inside the zip covers custom songs, the in-game keys, making levels and uninstalling.

| Key | In game |
|---|---|
| F1 | No-hit mode: restart the song on a hit |
| F2 | Hitbox overlay |
| F3 | Freeze on a hit instead of restarting |
| F5 | Reload the custom level being played |

Settings live in `BepInEx\config\local.jsabpractice.cfg`.

## The levels folder

The mod keeps everything in `Documents\JSAB Levels` (configurable):

| | |
|---|---|
| `packages/*.jsab` | Song packages, listed at the end of Practice |
| `custom/<LEVEL>.json` | Replaces that game level when played (offline) |
| `vanilla/<LEVEL>.json` | Every level the game loads, dumped as JSON |
| `_hazard-catalog.json` | The hazard types and their parameters (the editor needs it) |
| `recordings/` | Recorded hazard motion, if `RecordMotion` is on |
| `music/`, `art/` | Game music and art for the editor, from the Python tools below |

A `.jsab` is a zip with `level.json` (`{format, id, name, artist, author, difficulty, remixOf?, normal: {spawns}, hard?: {spawns}}`) and `music.ogg`, `.mp3` or `.wav` (absent for a remix).

## Building

**Plugin** (`JsabPractice/`, .NET 6, BepInEx 6 IL2CPP). Install the release zip into the game and launch it once, so BepInEx generates the interop assemblies the project references. Then:

```
cd JsabPractice
dotnet build -c Release
```

The build copies the plugin into the game's `BepInEx\plugins\JsabPractice` (close the game first; `-p:Deploy=false` skips the copy). The game path defaults to the usual Steam folder; pass `-p:GameDir=...` if yours differs.

**Release zip:** `python tools/make_release.py` writes `dist/jsab-mod-<version>.zip`.

**Editor** (`editor/index.html`): one self-contained file, no build step. Open it in Chrome or Edge. Tests run it in headless Chrome:

```
python editor/test/run_tests.py
```

They need Chrome, plus `vanilla/MUSIC_FOX.json` and `_hazard-catalog.json` in the levels folder.

**Python tools** (`tools/`, `pip install -r tools/requirements.txt`):

| | |
|---|---|
| `extract_levels.py` | Every level and its music straight from the game files, into `vanilla/` and `music/` (no need to play them) |
| `extract_art.py` | The hazard art into `art/`, so the editor draws hazards as the game does |
| `make_package.py` | Pack level files and music into a `.jsab` from the command line |
| `hazard_art.py` | Rebuilds `JsabPractice/hazard-art.json` (art bundles each hazard type needs) |
| `decomp.py`, `method_map.py`, `ghidra/` | Reading the game's code with Ghidra, for porting hazard behaviour into the editor |

## Layout

- `JsabPractice/`: the BepInEx plugin (practice tools, level dump/replace, song packages, motion recorder, the local editor bridge on `127.0.0.1:47321`)
- `editor/`: the level editor and its tests
- `tools/`: Python tools; `release/`: what goes into the release zip besides the builds

## Third-party

The release zip ships BepInEx (LGPL-2.1), NVorbis (MIT) and NLayer (MIT) unmodified; see `release/THIRD-PARTY-NOTICES.txt`.
