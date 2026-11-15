"""Evaluate JavaScript in the editor with a level (and its recording) open, headless.

usage: python editor/test/eval.py LEVEL "js expression"

The expression runs after vanilla\\LEVEL.json, its recording (room seed recovered) and the
hazard catalog are loaded; its value is printed as JSON. For inspecting what the editor
works out (simObjects(), placeOf(s), gameRand(s, n), ...) without the browser UI, e.g.
  python editor/test/eval.py MUSIC_FOX "simObjects().filter(o => o.t0 < 14100 && o.t1 > 14100).length"
"""
import json, os, re, subprocess, sys, tempfile

EDITOR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'index.html')
LEVELS = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
url = lambda *p: 'file:///' + os.path.join(LEVELS, *p).replace('\\', '/')
level, expr = sys.argv[1], sys.argv[2]

script = '''
<script>
// No drawing loop: the page would never go idle, so --virtual-time-budget would run out only
// after ~24000 redraws of the stage and timeline (minutes to hours on a big level).
window.requestAnimationFrame = () => 0;
(async () => {
  const out = document.createElement('pre'); out.id = 'result'; document.body.appendChild(out);
  try {
    const get = async u => { const r = await fetch(u); return r.ok ? r.json() : null; };
    loadCatalog(await get(%(cat)s));
    setLevel(await get(%(lvl)s), 'vanilla/%(level)s.json');
    const rec = await get(%(rec)s).catch(() => null);
    if (rec) { S.rec = indexRecording(rec, 0); S.rec.roomSeed ??= recoverRoomSeed(S.rec); S.rolledSeed = false; noteRecordingBundles(S.rec); }
    // The exported art (S.art), with every document loaded, for drawing tests.
    const dir = { getFileHandle: async name => ({ getFile: async () => (await fetch(%(art)s + '/' + name)).blob() }) };
    S.art = artLibrary(await get(%(art)s + '/index.json'), dir);
    // waitArtDoc(bundle): the editor's artDoc once it has loaded.
    globalThis.waitArtDoc = async bundle => { let d = artDoc(bundle); for (let i = 0; i < 4000 && !d; i++) { await new Promise(r => setTimeout(r, 50)); d = artDoc(bundle); } return d; };
    const value = await (async () => (%(expr)s))();
    out.textContent = JSON.stringify(value);
  } catch (e) { out.textContent = 'ERROR ' + e.stack; }
})();
</script>''' % dict(cat=json.dumps(url('_hazard-catalog.json')), lvl=json.dumps(url('vanilla', level + '.json')),
                    rec=json.dumps(url('recordings', level + '.json')), art=json.dumps(url('art')), level=level, expr=expr)

page = open(EDITOR, encoding='utf-8').read().replace('</body>', script + '</body>')
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'eval.html')
    open(path, 'w', encoding='utf-8').write(page)
    # A big level (Born Survivor: 13301 spawns) checked while other Chromes run can take more
    # than 10 minutes; a timeout is reported on stdout like any other failure. Its own profile
    # (user-data-dir): on the default one, a run while the user's Chrome is open could stall.
    try:
        # CHROME_DEBUG_PORT=N: DevTools on port N, to pause a run that hangs and read its stack.
        debug = ['--remote-debugging-port=' + os.environ['CHROME_DEBUG_PORT']] if os.environ.get('CHROME_DEBUG_PORT') else []
        dom = subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--user-data-dir=' + os.path.join(tmp, 'profile'), '--allow-file-access-from-files',
                              *debug, '--virtual-time-budget=400000', '--dump-dom', 'file:///' + path.replace('\\', '/')],
                             capture_output=True, text=True, encoding='utf-8', timeout=1800).stdout
    except subprocess.TimeoutExpired:
        print('ERROR headless Chrome took over 30 minutes'); sys.exit(1)
m = re.search(r'<pre id="result">(.*?)</pre>', dom, re.S)
print(m.group(1).replace('&lt;', '<').replace('&gt;', '>').replace('&amp;', '&') if m else 'no output')
