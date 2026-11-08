"""Render the editor's stage for a recorded level at some times, headless.

usage: python editor/test/render_stage.py LEVEL t1,t2,... [out_prefix]

Loads the editor headless (Chrome, file access on) with vanilla\\LEVEL.json, its
recording and the exported art, draws the stage at each time (seconds) with recorded
art on and the schematic off, and writes <prefix>_<t>.png to the current folder
(or RENDER_OUT). For checking recordings against the game by eye.
"""
import base64, os, re, subprocess, sys, tempfile, json
EDITOR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'index.html')
LEVELS = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
OUT = os.environ.get('RENDER_OUT') or os.getcwd()

level, times = sys.argv[1], [float(x) for x in sys.argv[2].split(',')]
prefix = sys.argv[3] if len(sys.argv) > 3 else 'ed_' + level
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
    const dir = { getFileHandle: async name => ({ getFile: async () => (await fetch(%(art)s + '/' + name)).blob() }) };
    S.art = artLibrary(await get(%(art)s + '/index.json'), dir);
    for (const id of ['showMotion', 'showSprites']) { const el = document.getElementById(id); if (el) el.checked = true; }
    const sch = document.getElementById('showSchematic'); if (sch) sch.checked = false;
    const sim = document.getElementById('simulate'); if (sim) sim.checked = false;
    const lines = [];
    for (const t of %(times)s) {
      S.t = t * 1000;
      for (let i = 0; i < 40; i++) { drawStage(); await new Promise(r => setTimeout(r, 100)); if (![...S.art.docs.values()].some(d => d instanceof Promise)) { if (i > 2) break; } }
      drawStage();
      lines.push(t + ' ' + stage.toDataURL('image/png'));
    }
    out.textContent = lines.join('\\n');
  } catch (e) { out.textContent = 'ERROR ' + e.stack; }
})();
</script>''' % dict(cat=json.dumps(url('_hazard-catalog.json')), lvl=json.dumps(url('vanilla', level + '.json')),
                    rec=json.dumps(url('recordings', level + '.json')), art=json.dumps(url('art')), level=level,
                    times=json.dumps(times))

page = open(EDITOR, encoding='utf-8').read().replace('</body>', script + '</body>')
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'render.html')
    open(path, 'w', encoding='utf-8').write(page)
    dom = subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--allow-file-access-from-files',
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
