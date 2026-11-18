"""Build the release zip that players extract into the game folder.

    python tools/make_release.py

Builds the plugin (Release, without copying it into the game), downloads the
BepInEx build it runs on (checked against a pinned SHA-256, cached in
tools/out/cache) and writes dist/jsab-mod-<version>.zip:

    BepInEx 6 be.791 (winhttp.dll, BepInEx/, dotnet/, ...)
    BepInEx/plugins/JsabPractice/  JsabPractice.dll, NVorbis.dll, NLayer.dll
    JSAB Level Editor.html
    JSAB Mod - README.txt, THIRD-PARTY-NOTICES.txt, licenses/

Standard library only.
"""
import hashlib
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PLUGIN = os.path.join(ROOT, 'JsabPractice')
RELEASE = os.path.join(ROOT, 'release')
CACHE = os.path.join(ROOT, 'tools', 'out', 'cache')
DIST = os.path.join(ROOT, 'dist')

# The BepInEx build the plugin is compiled and tested against.
BEPINEX_URL = 'https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip'
BEPINEX_SHA256 = 'bb1c510909b69522733791f3a0817a487379a52da6468a492f1c9c19b3ec6f9a'
PLUGIN_FILES = ['JsabPractice.dll', 'NVorbis.dll', 'NLayer.dll']


def version():
    src = open(os.path.join(PLUGIN, 'Plugin.cs'), encoding='utf-8').read()
    return re.search(r'Version = "([^"]+)"', src).group(1)


def dotnet():
    for d in (os.environ.get('DOTNET_ROOT'), os.path.join(os.environ.get('LOCALAPPDATA', ''), 'Microsoft', 'dotnet-sdk')):
        if d and os.path.isfile(os.path.join(d, 'dotnet.exe')):
            return os.path.join(d, 'dotnet.exe'), d
    exe = shutil.which('dotnet')
    if not exe:
        sys.exit('dotnet not found: install the .NET SDK or set DOTNET_ROOT')
    return exe, None


def build():
    exe, root = dotnet()
    env = dict(os.environ, **({'DOTNET_ROOT': root} if root else {}))
    subprocess.run([exe, 'build', '-c', 'Release', '-p:Deploy=false', '-nologo', '-v:q'], cwd=PLUGIN, env=env, check=True)
    out = os.path.join(PLUGIN, 'bin', 'Release')
    for f in PLUGIN_FILES:
        if not os.path.isfile(os.path.join(out, f)):
            sys.exit(f'build output is missing {f}')
    return out


def bepinex():
    os.makedirs(CACHE, exist_ok=True)
    path = os.path.join(CACHE, 'BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791.zip')
    if not os.path.isfile(path):
        print('Downloading BepInEx 6.0.0-be.791...')
        req = urllib.request.Request(BEPINEX_URL, headers={'User-Agent': 'jsab-mod-release'})
        with urllib.request.urlopen(req) as r, open(path + '.part', 'wb') as f:
            shutil.copyfileobj(r, f)
        os.replace(path + '.part', path)
    digest = hashlib.sha256(open(path, 'rb').read()).hexdigest()
    if digest != BEPINEX_SHA256:
        os.remove(path)
        sys.exit(f'BepInEx download has the wrong SHA-256 ({digest}); deleted it, try again')
    return path


def main():
    ver = version()
    out = build()
    bep = bepinex()
    os.makedirs(DIST, exist_ok=True)
    target = os.path.join(DIST, f'jsab-mod-{ver}.zip')
    readme = open(os.path.join(RELEASE, 'README.txt'), encoding='utf-8').read().replace('{VERSION}', ver)
    with zipfile.ZipFile(target, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        with zipfile.ZipFile(bep) as src:
            for info in src.infolist():
                if not info.is_dir():
                    z.writestr(info, src.read(info))
        for f in PLUGIN_FILES:
            z.write(os.path.join(out, f), f'BepInEx/plugins/JsabPractice/{f}')
        z.write(os.path.join(ROOT, 'editor', 'index.html'), 'JSAB Level Editor.html')
        z.writestr('JSAB Mod - README.txt', readme.replace('\n', '\r\n'))
        notices = open(os.path.join(RELEASE, 'THIRD-PARTY-NOTICES.txt'), encoding='utf-8').read()
        z.writestr('THIRD-PARTY-NOTICES.txt', notices.replace('\n', '\r\n'))
        for f in sorted(os.listdir(os.path.join(RELEASE, 'licenses'))):
            z.write(os.path.join(RELEASE, 'licenses', f), f'licenses/{f}')
    size = os.path.getsize(target) / 1e6
    print(f'{target} ({size:.1f} MB)')


if __name__ == '__main__':
    main()
