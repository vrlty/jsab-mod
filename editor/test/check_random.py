"""Check the editor's port of the game's random against a recording, headless.

usage: python editor/test/check_random.py LEVEL [LEVEL...]

Opens vanilla\\LEVEL.json and its recording in the editor (Chrome, file access on), lets the
editor recover the run's room seed from the recording, then compares where the editor puts
each RoundSpikes with a random spot (no position: setRandomPos; random margins:
setPosToSpawn) against where the recording saw it land. Prints the seed, how long
recovering it took and how far off the editor is.
"""
import json, os, re, subprocess, sys, tempfile

EDITOR = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'index.html')
LEVELS = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'
url = lambda *p: 'file:///' + os.path.join(LEVELS, *p).replace('\\', '/')

script = '''
<script>
(async () => {
  const out = document.createElement('pre'); out.id = 'result'; document.body.appendChild(out);
  const lines = [];
  try {
    const get = async u => (await fetch(u)).json();
    loadCatalog(await get(%(cat)s));
    for (const level of %(levels)s) {
      setLevel(await get(%(vanilla)s + '/' + level + '.json'), 'vanilla/' + level + '.json');
      const rec = indexRecording(await get(%(recs)s + '/' + level + '.json'), 0);
      S.rec = rec; S.rolledSeed = false;
      const t0 = performance.now();
      rec.roomSeed = recoverRoomSeed(rec);
      const ms = performance.now() - t0;
      // Spawns sharing a time can't be told apart in a recording: compare with the nearest
      // placement at that type and time.
      let n = 0, within = 0, worst = 0;
      for (const s of S.level.spawns) {
        if (s.type !== 'RoundSpikes') continue;
        const pls = rec.placements.get(`${s.type}|${s.timeMs}`);
        if (!pls?.length) continue;
        const p = s.pos, r = rangeOf(s);
        const noPos = !p || (!p.xUsed && !p.yUsed);
        const axes = noPos ? [1] : [isRange(r.x) && p.xUsed ? 0 : -1, isRange(r.y) && p.yUsed ? 1 : -1].filter(a => a >= 0);
        if (!axes.length) continue;
        const at = placeOf(s), got = [at.x, at.y];
        for (const a of axes) {
          const d = Math.min(...pls.map(pl => Math.abs(got[a] - pl[a])));
          n++; if (d < 8) within++; worst = Math.max(worst, d);
        }
      }
      lines.push(`${level}: room seed ${rec.roomSeed} (${ms.toFixed(0)} ms); ${within}/${n} random coordinates within 8 px, worst ${worst.toFixed(2)}`);
    }
  } catch (e) { lines.push('ERROR ' + e.stack); }
  out.textContent = lines.join('\\n');
})();
</script>''' % dict(cat=json.dumps(url('_hazard-catalog.json')), vanilla=json.dumps(url('vanilla')),
                    recs=json.dumps(url('recordings')), levels=json.dumps(sys.argv[1:]))

page = open(EDITOR, encoding='utf-8').read().replace('</body>', script + '</body>')
with tempfile.TemporaryDirectory() as tmp:
    path = os.path.join(tmp, 'check.html')
    open(path, 'w', encoding='utf-8').write(page)
    dom = subprocess.run([CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--allow-file-access-from-files',
                          '--virtual-time-budget=300000', '--dump-dom', 'file:///' + path.replace('\\', '/')],
                         capture_output=True, text=True, encoding='utf-8', timeout=900).stdout
m = re.search(r'<pre id="result">(.*?)</pre>', dom, re.S)
print(m.group(1) if m else 'no output')
