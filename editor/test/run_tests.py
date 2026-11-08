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
  // Recorded tracks drawn with captured art.
  const art = document.createElement('canvas'); art.width = 40; art.height = 40;
  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: { '1': 'ANIM_A', '2': 'ANIM_B' }, samples: [
    { t: 1000, c: [1, 100, 100, 20, 3, 300, 300, 10], q: [2, 0, 0, 100, 0, 100, 40, 0, 40] },
  ] }, 0);
  S.recFresh = true;
  S.sprites = { level: levelName(), byAnim: {
    ANIM_A: { active: { img: art, width: 40, height: 40, originX: 20, originY: 20 } },
    ANIM_B: { active: { img: art, width: 40, height: 20, originX: 0, originY: 0 } },
  } };
  calls.length = 0;
  drawMotion(fake, 1000);
  const images = calls.filter(c => c[0] === 'drawImage');
  check('tracks with art are drawn as sprites', images.length === 2, images.length);
  check('circle sprite centred and scaled to the hitbox', JSON.stringify(images[0].slice(2)) === JSON.stringify([75, 75, 50, 50]), JSON.stringify(images[0].slice(2)));
  check('untagged circle without round-spike art stays a hitbox', calls.some(c => c[0] === 'arc' && c[1] === 300));
  S.sprites.byAnim.ENEMY_ROUND_SPIKES = { active: { img: art, width: 40, height: 40, originX: 20, originY: 20 } };
  calls.length = 0;
  drawMotion(fake, 1000);
  check('untagged small circle uses round-spike art', calls.filter(c => c[0] === 'drawImage').length === 3);
  delete S.sprites.byAnim.ENEMY_ROUND_SPIKES;
  $('#showSprites').checked = false;
  calls.length = 0;
  drawMotion(fake, 1000);
  check('Art toggle off draws plain hitboxes', !calls.some(c => c[0] === 'drawImage'));
  $('#showSprites').checked = true;
  S.sprites = null;

  // Exported game art: Flash playback rules, tints, rotated sprites, collision circles.
  const atlas = document.createElement('canvas'); atlas.width = 4; atlas.height = 2;
  { const g = atlas.getContext('2d');
    g.fillStyle = '#ff0000'; g.fillRect(0, 0, 2, 2);
    g.fillStyle = '#00ff00'; g.fillRect(2, 0, 1, 1);
    g.fillStyle = '#0000ff'; g.fillRect(3, 0, 1, 1); }
  const child = { c: 'CHILD', i: 0 };
  const doc = prepareArtDoc({ sprites: [[0, 0, 2, 2, 0], [2, 0, 2, 1, 90]], symbols: {
    CHILD: { frames: [0, 1, 2, 3, 4].map(f => [{ s: 0, r: [f, 0, 2, 2] }]) },   // r[0] tells the frame
    PARENT: { frames: [[], [], [child], [child]] },
    HOLD: { frames: [[child], [child], [child]], stops: [1] },
    ALWAYS: { frames: [[child], [child]] },
    TINT: { frames: [[{ s: 0, r: [0, 0, 2, 2], t: [0, 0, 1, 0.5] }]] },
    NESTTINT: { frames: [[{ c: 'TINT', i: 0, t: [0, 1, 0, 0.5], a: 0.5 }]] },
    ROT: { frames: [[{ s: 1, r: [0, 0, 1, 2] }]] },
    COLSYM: { frames: [[{ s: 0, r: [-20, -20, 40, 40] }, { c: 'COL_OBJECT_CIRCLE', i: 0, m: [0.4, 0, 0, 0.4, 3, 4] }]] },
    COL_OBJECT_CIRCLE: { frames: [] },
  } }, atlas);
  const childFrame = (name, ticks) => { let f = null; artWalk(doc, name, ticks, M_IDENT, CT_NONE, o => { if (o.s !== undefined) f = o.r[0]; }); return f; };
  check('nested clip starts when its instance appears', childFrame('PARENT', 3) === 1, childFrame('PARENT', 3));
  check('nested clip restarts when its instance comes back', childFrame('PARENT', 6) === 0, childFrame('PARENT', 6));
  check('parent nothing drawn before the instance appears', childFrame('PARENT', 1) === null);
  check('stop frame holds the parent, child keeps playing', childFrame('HOLD', 10) === 0 && childFrame('HOLD', 12) === 2, `${childFrame('HOLD', 10)} ${childFrame('HOLD', 12)}`);
  check('instance on every frame plays from the start', childFrame('ALWAYS', 7) === 2, childFrame('ALWAYS', 7));
  check('duration covers nested clips', artDuration(doc, 'PARENT') === 5 && artDuration(doc, 'TINT') === 1);
  let nct = null; artWalk(doc, 'NESTTINT', 0, M_IDENT, CT_NONE, (o, m, ct) => { nct = ct; });
  check('tints compose as multiply + offset', JSON.stringify(nct) === JSON.stringify([0.25, 0, 0.5, 0.25, 0.5]), JSON.stringify(nct));
  const px = document.createElement('canvas'); px.width = px.height = 2;
  const pg = px.getContext('2d', { willReadFrequently: true });
  drawArt(pg, doc, 'TINT', 0);
  const tp = [...pg.getImageData(0, 0, 1, 1).data];
  check('tint drawn: red half-tinted blue', Math.abs(tp[0] - 128) <= 2 && tp[1] === 0 && Math.abs(tp[2] - 128) <= 2 && tp[3] === 255, tp);
  const rs = artSprite(doc, 1, CT_NONE).getContext('2d').getImageData(0, 0, 1, 2).data;
  check('rotated sprite stood upright (left of the packed pair on top)', rs[1] === 255 && rs[6] === 255 && rs[0] === 0 && rs[4] === 0, [...rs]);
  const cc = artColCircle(doc, 'COLSYM', 0);
  check('collision circle found in the art', cc && cc.r === 10 && cc.x === 3 && cc.y === 4, JSON.stringify(cc));
  check('extent ignores collision shapes', JSON.stringify(artExtent(doc, 'COLSYM', 0)) === '[-20,-20,20,20]', JSON.stringify(artExtent(doc, 'COLSYM', 0)));
  S.art = artLibrary({ docs: { jsb_ingamevisuals_2: { symbols: { X: 1, Y: 1 } }, jsb_ingamevisuals: { symbols: { X: 1 } }, other: { symbols: { Y: 1, Z: 1 } } } });
  check('art resolves to the named document first', artBundleFor('X', 'jsb_ingamevisuals_2') === 'jsb_ingamevisuals_2');
  check('art falls back to the main in-game document', artBundleFor('X') === 'jsb_ingamevisuals' && artBundleFor('Y') === 'jsb_ingamevisuals_2' && artBundleFor('Z') === 'other' && artBundleFor('Q') === null);
  S.art = null;

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
