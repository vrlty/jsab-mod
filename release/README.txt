JSAB Mod {VERSION}
Practice tools, custom songs and a level editor for Just Shapes & Beats
(the Steam version, on Windows).

Source and updates: https://github.com/vrlty/jsab-mod
Not affiliated with Berzerk Studio. You need your own copy of the game.


INSTALL
-------
1. Close the game.
2. Open the game's folder: in Steam, right-click Just Shapes & Beats >
   Manage > Browse local files.
3. Extract everything in this zip into that folder, next to JSB.exe. It adds
   BepInEx (the mod loader), the mod, and "JSAB Level Editor.html".
4. Start the game. The first launch needs an internet connection and takes a
   minute or two while BepInEx sets itself up; later launches are quick.
   A console window opens next to the game: that's BepInEx's log. To hide it,
   set Enabled = false under [Logging.Console] in BepInEx\config\BepInEx.cfg.


PLAYING CUSTOM SONGS
--------------------
Song packages are .jsab files. Put them in Documents\JSAB Levels\packages
(the folder appears after the first launch), or use Import in the editor.
They're at the end of the game's Practice list.


PRACTICE KEYS (in game)
-----------------------
F1  No-hit mode: restart the song as soon as you take a hit.
F2  Show hitboxes (hazards red, you cyan).
F3  Freeze on a hit instead of restarting, to see what got you
    (Space or Enter restarts).
F5  Reload the custom level of the song you're playing.

All of these start off. Settings (keys, delays, folders):
BepInEx\config\local.jsabpractice.cfg, created on the first launch.


MAKING LEVELS
-------------
1. Play any level once with the mod, so it writes the list of hazard types
   to Documents\JSAB Levels.
2. Open "JSAB Level Editor.html" (in the game folder) in Chrome or Edge.
   Firefox can't open folders, so it won't work there.
3. Click "Open levels folder" and pick Documents\JSAB Levels.
4. "New song..." starts a song with your own music (.ogg, .mp3 or .wav) or a
   remix of a game song. Save writes it to the packages folder; Export...
   saves a copy of the .jsab to send to friends.
5. With the game running, "Preview in game" (P) plays your level in the game
   from the playhead. "Shortcuts and help" in the editor's ... menu explains
   the rest.

You can also change a game level: open it in the editor and save. The copy
goes to Documents\JSAB Levels\custom and replaces that level when you play it
(F5 reloads it while playing).


UNINSTALL
---------
Delete winhttp.dll from the game folder: the game then starts without any
mods. To remove everything, also delete the BepInEx, dotnet and licenses
folders, doorstop_config.ini, .doorstop_version, changelog.txt,
"JSAB Level Editor.html", "JSAB Mod - README.txt" and THIRD-PARTY-NOTICES.txt.
Your levels stay in Documents\JSAB Levels.


Third-party software in this zip: see THIRD-PARTY-NOTICES.txt.
