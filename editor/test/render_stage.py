"""Render the editor's stage for a recorded level at some times, headless.

usage: python editor/test/render_stage.py LEVEL t1,t2,... [out_prefix] [--sim]

Loads the editor headless (Chrome, file access on) with vanilla\\LEVEL.json, its
recording and the exported art, draws the stage at each time (seconds) with recorded
art on and the schematic off, and writes <prefix>_<t>.png to the current folder
(or RENDER_OUT). For checking recordings against the game by eye.
--sim draws the editor's simulation instead of the recording (to compare the two).
"""
import base64, os, re, subprocess, sys, tempfile, json
EDITOR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'index.html')
LEVELS = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
OUT = os.environ.get('RENDER_OUT') or os.getcwd()

SIM = '--sim' in sys.argv
argv = [a for a in sys.argv if a != '--sim']
level, times = argv[1], [float(x) for x in argv[2].split(',')]
prefix = argv[3] if len(argv) > 3 else ('sim_' if SIM else 'ed_') + level
url = lambda *p: 'file:///' + os.path.join(LEVELS, *p).replace('\\', '/')

script = '''
<script>
(async () => {
  const out = document.createElement('pre'); out.id = 'shots'; document.body.appendChild(out);
  try {
    const get = async u => (await fetch(u)).json();
    loadCatalog(await get(%(cat)s));
    setLevel(await get(%(lvl)s), 'vanilla/%(level)s.json');
    S.rec = indexRecording(await get(%(rec)s), 0);
    S.recFresh = true;
    // Same random rolls as the recorded run, so simulated random positions line up.
    S.rec.roomSeed ??= recoverRoomSeed(S.rec); S.rolledSeed = false; noteRecordingBundles(S.rec);
    const dir = { getFileHandle: async name => ({ getFile: async () => (await fetch(%(art)s + '/' + name)).blob() }) };
    S.art = artLibrary(await get(%(art)s + '/index.json'), dir);
    for (const id of ['showMotion', 'showSprites']) { const el = document.getElementById(id); if (el) el.checked = true; }
    const sch = document.getElementById('showSchematic'); if (sch) sch.checked = false;
    const sim = document.getElementById('simulate'); if (sim) sim.checked = %(sim)s;
    if (%(sim)s) document.getElementById('showMotion').checked = false;
    const lines = [];
    for (const t of %(times)s) {
      S.t = t * 1000;
      // Wait on the art loads themselves: virtual time (--virtual-time-budget) runs past
      // decompression and image decoding, so a timed wait gives up before the art is in.
      for (let i = 0; i < 20; i++) {
        drawStage();
        const loading = [...S.art.docs.values()].filter(d => d instanceof Promise);
        if (!loading.length && i > 1) break;
        await Promise.allSettled(loading);
        await new Promise(r => setTimeout(r, 50));
      }
      drawStage();
      lines.push(t + ' ' + stage.toDataURL('image/png'));
    }
    out.textContent = lines.join('\\n');
  } catch (e) { out.textContent = 'ERROR ' + e.stack; }
})();
</script>''' % dict(cat=json.dumps(url('_hazard-catalog.json')), lvl=json.dumps(url('vanilla', level + '.json')),
                    rec=json.dumps(url('recordings', level + '.json')), art=json.dumps(url('art')), level=level,
                    times=json.dumps(times), sim='true' if SIM else 'false')

page = open(EDITOR, encoding='utf-8').read().replace('</body>', script + '</body>')
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'render.html')
    open(path, 'w', encoding='utf-8').write(page)
    dom = subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--user-data-dir=' + os.path.join(tmp, 'profile'), '--allow-file-access-from-files',
                          '--virtual-time-budget=120000', '--window-size=1700,1100', '--dump-dom', 'file:///' + path.replace('\\', '/')],
                         capture_output=True, text=True, encoding='utf-8', timeout=600).stdout
m = re.search(r'<pre id="shots">(.*?)</pre>', dom, re.S)
if not m:
    print('no output'); sys.exit(1)
text = m.group(1)
if text.startswith('ERROR'):
    print(text); sys.exit(1)
for line in text.strip().splitlines():
    t, data = line.split(' ', 1)
    p = os.path.join(OUT, f'{prefix}_{float(t):.0f}.png')
    open(p, 'wb').write(base64.b64decode(data.split(',', 1)[1]))
    print(p)
