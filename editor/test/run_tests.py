"""Headless browser tests for the level editor.

Injects a test script into a copy of editor/index.html together with a real vanilla
level and the hazard catalog, loads it in headless Chrome and prints the results.

    python editor/test/run_tests.py [path-to-jsab-levels]
"""
import html
import os
import re
import subprocess
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
EDITOR = os.path.join(HERE, '..', 'index.html')
DEFAULT_LEVELS = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
CHROME = r'C:\Program Files\Google\Chrome\Application\chrome.exe'

TESTS = r'''
<script>
const R = [];
window.addEventListener('error', e => R.push('FAIL uncaught error: ' + e.message));
function check(name, cond, extra = '') { R.push((cond ? 'PASS ' : 'FAIL ') + name + (extra !== '' ? ' :: ' + extra : '')); }
const plain = spawns => JSON.stringify(JSON.parse(serialize().text).spawns) === JSON.stringify(spawns);
(async () => {
try {
  const LEVEL = JSON.parse(document.getElementById('levelsrc').textContent);
  const CAT = JSON.parse(document.getElementById('catsrc').textContent);
  loadCatalog(CAT);
  check('catalog loaded', S.catalog.length > 100, S.catalog.length);
  setLevel(JSON.parse(JSON.stringify(LEVEL)), 'test');
  check('level loaded', S.level.spawns.length === LEVEL.spawns.length, S.level.spawns.length);
  check('unedited level round-trips byte-identical', plain(LEVEL.spawns));

  addAt('Wall', 5000);
  const w = [...S.selected][0];
  check('add wall at 5s', S.level.spawns.length === LEVEL.spawns.length + 1 && w.type === 'Wall' && w.timeMs === '5000');
  check('wall gets template params', 'modelSide' in w.params && 'size' in w.params, JSON.stringify(w.params));
  check('wall template position is not empty', !!w.pos && (!w.pos.xUsed || w.pos.x !== '') && (!w.pos.yUsed || w.pos.y !== ''), JSON.stringify(w.pos));
  check('inspector shows fields', document.querySelectorAll('#inspBody [data-f]').length > 5);

  const side = document.querySelector('#inspBody [data-f="param:modelSide"]');
  side.value = 'left';
  side.dispatchEvent(new Event('change', { bubbles: true }));
  check('edit a param through the inspector', w.params.modelSide === 'left');
  check('changing a wall to left/right moves its position to y', w.pos.yUsed && w.pos.y !== '' && !w.pos.xUsed, JSON.stringify(w.pos));
  check('two undo steps recorded', S.undo.length === 2, S.undo.length);
  undo(); undo();
  check('undo restores the original exactly', S.level.spawns.length === LEVEL.spawns.length && plain(LEVEL.spawns));
  redo();
  check('redo', S.level.spawns.length === LEVEL.spawns.length + 1);

  S.selected.clear();
  addAt('Wall', 500);
  const neg = [...S.selected][0];
  mutate(() => { neg.warningMs = '2000'; });
  check('warning before 0:00 is flagged', S.issues.some(i => i[0] === neg), S.issues.length);
  mutate(() => changeType(neg, 'CamBeatEffect'));
  check('type change drops unsupported position', neg.pos === null && neg.type === 'CamBeatEffect', JSON.stringify(neg));

  const redoWall = S.level.spawns.find(s => s.type === 'Wall' && s.timeMs === '5000');
  S.selected = new Set([redoWall]);
  copySelection();
  const n0 = S.level.spawns.length;
  pasteAt(10000);
  check('paste at playhead', S.level.spawns.length === n0 + 1 && Math.abs(T([...S.selected][0]) - snapT(10000)) < 1e-6);
  duplicateSelection();
  check('duplicate', S.level.spawns.length === n0 + 2);

  const beams = S.level.spawns.filter(s => s.type === 'Beam').slice(0, 5);
  S.selected = new Set(beams);
  renderInspector();
  const before = beams.map(T);
  document.querySelector('#mShift').value = '-100';
  document.querySelector('#inspBody [data-act="shift"]').click();
  const after = beams.map(T);
  // Times are stored to 0.001 ms.
  check('multi-select time shift', beams.length > 0 && after.every((t, i) => Math.abs(t - (before[i] - 100)) < 1e-3), `${beams.length} beams, before ${before} after ${after}`);

  const rw = newSpawn('Wall', 20000);
  rw.params.modelSide = 'down'; fixWallPos(rw); rw.pos.xUsed = false;
  check('unused coordinate is a full-edge range', JSON.stringify(rangeOf(rw).x) === '[0,1280]', JSON.stringify(rangeOf(rw)));
  const bh = newSpawn('BlackHole', 20000);
  bh.pos = { ...bh.pos, x: '960', xUsed: true, xRandomMargin: 100 };
  check('random margin gives centre ± margin', JSON.stringify(rangeOf(bh).x) === '[860,1060]', JSON.stringify(rangeOf(bh)));
  check('camera effects stay non-positional', !isPositional(newSpawn('CamBeatEffect', 0)));
  S.level.spawns.push(rw, bh);
  S.t = 20000; S.selected = new Set([rw]); drawStage();
  S.selected.clear(); drawStage();

  S.t = 14500; S.selected.clear(); drawStage(); drawTimeline();
  S.selected = new Set([redoWall]); drawStage();
  check('stage and timeline render', true);

  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, samples: [
    { t: 1000, c: [1, 100, 100, 20], q: [2, 0, 0, 10, 0, 10, 10, 0, 10] },
    { t: 1050, c: [1, 200, 100, 20], q: [] },
    { t: 5000, c: [], q: [] },
  ] }, 0);
  check('recording ranges', JSON.stringify(S.rec.ranges) === '[[1000,1050],[5000,5000]]', JSON.stringify(S.rec.ranges));
  const calls = [];
  const fake = new Proxy({}, { get: (o, k) => k in o ? o[k] : (...args) => calls.push([k, ...args]), set: (o, k, v) => (o[k] = v, true) });
  drawMotion(fake, 1025);
  const arc = calls.find(c => c[0] === 'arc');
  check('motion interpolates between samples (y down)', arc && Math.abs(arc[1] - 150) < 1e-6 && Math.abs(arc[2] - 100) < 1e-6, JSON.stringify(arc));
  check('quad without a match in the next sample is still drawn', calls.filter(c => c[0] === 'lineTo').length === 3);
  calls.length = 0;
  drawMotion(fake, 3000);
  check('nothing drawn in a recording gap', calls.length === 0, calls.length);
  const sig = await currentSignature();
  check('signature for edited content is a custom hash', /^custom:[0-9a-f]{16}$/.test(sig), sig);
  S.t = 1025; drawStage(); drawTimeline();
  S.rec = null;

  detectBpm();
  check('BPM detection in a sane range', S.snapCfg.bpm > 60 && S.snapCfg.bpm < 220, `${S.snapCfg.bpm} bpm, offset ${S.snapCfg.offset}`);

  const out = JSON.parse(serialize().text);
  check('saved spawns are in game order', out.spawns.every((s, i, a) => !i ||
    parseFloat(a[i - 1].timeMs) - parseFloat(a[i - 1].warningMs) <= parseFloat(s.timeMs) - parseFloat(s.warningMs)));
} catch (e) { R.push('FAIL exception: ' + e.stack); }
})();
setTimeout(() => {
  const d = document.createElement('pre');
  d.id = 'results';
  d.textContent = R.join('\n');
  document.body.appendChild(d);
}, 300);
</script>
'''


def embed(element_id, path):
    text = open(path, encoding='utf-8').read().replace('</', '<\\/')
    return f'<script type="application/json" id="{element_id}">{text}</script>'


def main():
    levels = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_LEVELS
    level = os.path.join(levels, 'vanilla', 'MUSIC_FOX.json')
    catalog = os.path.join(levels, '_hazard-catalog.json')
    page = open(EDITOR, encoding='utf-8').read()
    page = page.replace('</body>', embed('levelsrc', level) + embed('catsrc', catalog) + TESTS + '</body>')

    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, 'editor-test.html')
        open(path, 'w', encoding='utf-8').write(page)
        dom = subprocess.run(
            [CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--virtual-time-budget=4000',
             '--window-size=1600,1000', '--dump-dom', 'file:///' + path.replace('\\', '/')],
            capture_output=True, text=True, encoding='utf-8', timeout=120).stdout

    m = re.search(r'<pre id="results">(.*?)</pre>', dom, re.S)
    if not m:
        print('No results (page failed to run).')
        return 1
    results = html.unescape(m.group(1))
    print(results)
    failed = sum(line.startswith('FAIL') for line in results.splitlines())
    print(f'\n{failed} failed' if failed else '\nall passed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
