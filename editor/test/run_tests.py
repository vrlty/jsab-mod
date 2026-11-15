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
  // MetaSpawnParamPosition.setPosToSpawn: x + margin * roll (Art of War recording: 294/294).
  check('random margin spans x .. x + margin', JSON.stringify(rangeOf(bh).x) === '[960,1060]', JSON.stringify(rangeOf(bh)));
  check('camera effects stay non-positional', !isPositional(newSpawn('CamBeatEffect', 0)));
  const beam = newSpawn('Beam', 0); beam.params.size = '100';
  const wall100 = newSpawn('Wall', 0); wall100.params.size = '100';
  check('beam drawn at its in-game width (size 100 -> 24)', Math.abs(bandWidth(beam) - 24) < 1e-9, bandWidth(beam));
  check('wall drawn at its warning band width (size 100 -> 164)', Math.abs(bandWidth(wall100) - 164) < 1e-9, bandWidth(wall100));
  // Roll random: one concrete value inside the range, stable until another run is shown.
  const r1 = rolledRange(bh), r2 = rolledRange(bh);
  check('rolled position inside its range', r1.x >= 960 && r1.x <= 1060 && typeof r1.x === 'number', JSON.stringify(r1));
  check('rolled position stable between frames', r1.x === r2.x && r1.y === r2.y);
  const fixedBh = newSpawn('BlackHole', 20000);
  fixedBh.pos = { ...fixedBh.pos, x: '5', y: '7', xUsed: true, yUsed: true, xRandomMargin: 0, yRandomMargin: 0 };
  check('fixed coordinates are not rolled', JSON.stringify(rolledRange(fixedBh)) === '{"x":5,"y":7}', JSON.stringify(rolledRange(fixedBh)));
  {
    const seed0 = S.roomSeed, rolled0 = S.rolledSeed;
    S.roomSeed = 4242; S.rolledSeed = true;
    check('another run gives another position', rolledRange(bh).x !== r1.x);
    S.roomSeed = seed0; S.rolledSeed = rolled0;
  }
  // The game's random, exact. Expected values: UnityEngine.Random sampled in game, the first
  // live-traced spike (room 815142, idInSong 0: seed 915142, y -64.936 from centre), and
  // the Python port that matched recordings to the pixel (incl. a negative seed).
  check('UnityEngine.Random port: seed 0', Math.abs(unityValue(0) - 0.58413965) < 1e-7, unityValue(0));
  check('UnityEngine.Random port: seed 12345', Math.abs(unityValue(12345) - 0.5868507) < 1e-7, unityValue(12345));
  check('game seed: first traced spike', gameSeed(815142, 0, 0, 0) === 915142 && Math.abs(unityValue(915142) * 720 - 360 - -64.93594) < 1e-3, gameSeed(815142, 0, 0, 0));
  check('game seed: float rounding of idInSong * 161803398', gameSeed(646736, 300, 2, 0) === 1297625922, gameSeed(646736, 300, 2, 0));
  check('game seed: negative seed', gameSeed(646736, 1000, 0, -2) === -1404612428 && Math.abs(unityValue(-1404612428) - 0.20658471662816008) < 1e-7, gameSeed(646736, 1000, 0, -2));
  check('game seed: randomOffset term', gameSeed(185464, 140, 77, -1) === 1178424855, gameSeed(185464, 140, 77, -1));
  check('half-degree trig tables', myCos(90) === TRIG.cos[180] && mySin(180) === 0 && myCos(359.9) === TRIG.cos[719] && myCos(-90) === myCos(270));
  // Rolling uses where the recorded run put each spawn, matched by type, time and order.
  {
    const b1 = newSpawn('Beam', 30000), b2 = newSpawn('Beam', 30000), w9 = newSpawn('Wall', 31000);
    b1.timeMs = b2.timeMs = '30000'; w9.timeMs = '31000';
    w9.params.modelSide = 'any';
    S.level.spawns.push(b1, b2, w9);
    S.rec = indexRecording({ format: 9, level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: {}, samples: [{ t: 0, c: [], q: [] }],
      placements: [['Beam', '30000', 100, 820, 270], ['Beam', '30000', 900, 820, 270], ['Wall', '31000', 1280, 400, 180]] }, 0);
    const pl = recordedPlacements();
    check('placements matched in order among equal type and time', pl.get(b1)?.[0] === 100 && pl.get(b2)?.[0] === 900, JSON.stringify([pl.get(b1), pl.get(b2)]));
    check('wall rotation gives its side', sideFromRotation(180) === 'left' && sideFromRotation(90) === 'down' && sideFromRotation(-90) === 'up' && sideFromRotation(720) === 'right');
    S.level.spawns.splice(S.level.spawns.length - 3, 3);
    S.rec = null;
  }
  check('recording file follows the opened version', recordingFile('MUSIC_FOX') === (S.level.source?.startsWith('vanilla/') ? 'MUSIC_FOX.json' : 'MUSIC_FOX.custom.json'), recordingFile('MUSIC_FOX'));
  // Editor drawing toggle: only a selected hazard is drawn while it's off.
  {
    const orig = drawHazard;
    let n = 0;
    drawHazard = (...a) => (n++, orig(...a));
    S.level.spawns.push(rw, bh);
    S.t = 20000; S.selected.clear();
    $('#showSchematic').checked = false; drawStage();
    check('editor drawing off hides hazards', n === 0, n);
    S.selected = new Set([bh]); n = 0; drawStage();
    check('editor drawing off keeps the selected hazard', n === 1, n);
    $('#showSchematic').checked = true; S.selected.clear(); n = 0; drawStage();
    check('editor drawing on draws hazards', n >= 2, n);
    $('#rollRandom').checked = true; drawStage(); $('#rollRandom').checked = false;
    check('rolled stage renders', true);
    S.level.spawns.splice(S.level.spawns.indexOf(rw), 2);
    drawHazard = orig;
  }
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
  {
    // A busy moment: samples 2-4 skipped, then visuals eased from the snapshots around it.
    const node = x => [[1, 0, 1, 1, [1, 0, 0, 1, x, 100]]];
    const r = indexRecording({ level: 'X', signature: 'vanilla', sampleMs: 33.3, format: 9, symbols: [['A', 'b']], samples: [
      { t: 0, c: [], q: [], v: node(0) }, { t: 33, c: [], q: [], v: node(10) }, { t: 66, c: [], q: [], v: node(10) },
      { t: 100, c: [], q: [], v: node(10) }, { t: 200, c: [], q: [], v: node(50) },
      { t: 233, c: [], q: [], v: node(50) }, { t: 266, c: [], q: [], v: node(50) }, { t: 300, c: [], q: [], v: node(50) },
      { t: 333, c: [], q: [], v: node(50) }, { t: 366, c: [], q: [], v: node(50) }] }, 0);
    check('short gap eased over', r.ranges.length === 1, JSON.stringify(r.ranges));
    check('repeated snapshots dropped, a scene held still kept',
      JSON.stringify(r.samples.map(s => !!s.v)) === '[true,true,false,false,true,true,true,true,true,true]',
      JSON.stringify(r.samples.map(s => !!s.v)));
    check('nearest snapshots found', r.vPrev[3] === 1 && r.vNext[2] === 4, `${r.vPrev[3]} ${r.vNext[2]}`);
  }
  {
    // Format 10: a scrolling level's samples carry the camera; the grid moves with it.
    const saved = S.rec;
    S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, format: 10, samples: [
      { t: 0, c: [], q: [] }, { t: 50, c: [], q: [], k: [1, 0, 0, 1, -100, 0] }, { t: 100, c: [], q: [], k: [1, 0, 0, 1, -200, 0] }] }, 0);
    check('camera kept from the recording', S.rec.hasCamera && S.rec.samples[1].k?.[4] === -100);
    check('no camera at rest', cameraAt(0) === null);
    check('camera eased between samples', Math.abs(cameraAt(75)?.[4] + 150) < 1e-6, JSON.stringify(cameraAt(75)));
    const lines = [];
    const g = new Proxy({}, { get: (o, k) => k in o ? o[k] : (...args) => lines.push([k, ...args]), set: (o, k, v) => (o[k] = v, true) });
    drawWorldGrid(g, cameraAt(75), 1);
    const tf = lines.find(c => c[0] === 'transform');
    check('grid drawn through the camera', tf && tf[5] === -150, JSON.stringify(tf));
    const xs = lines.filter(c => c[0] === 'moveTo').map(c => c[1]);
    check('grid covers the scrolled view', Math.min(...xs) <= 150 && Math.max(...xs) >= 1280 + 150 - 80, `${Math.min(...xs)} ${Math.max(...xs)}`);
    S.rec = saved;
  }
  const withPlayer = indexRecording({ level: 'X', signature: 'vanilla', sampleMs: 50, tracks: { '9': 'HERO', '1': 'ENEMY_BEAM' }, samples: [
    { t: 0, c: [9, 10, 10, 75, 1, 20, 20, 5], q: [9, 0, 0, 1, 0, 1, 1, 0, 1] }] }, 0);
  check('player\'s own dash shape dropped from old recordings', JSON.stringify([...withPlayer.samples[0].c]) === '[1,20,20,5]' && withPlayer.samples[0].q.length === 0,
    JSON.stringify([...withPlayer.samples[0].c]));
  const calls = [];
  const fake = new Proxy({}, { get: (o, k) => k in o ? o[k] : (...args) => calls.push([k, ...args]), set: (o, k, v) => (o[k] = v, true) });
  drawMotion(fake, 1025);
  const arc = calls.find(c => c[0] === 'arc');
  check('motion interpolates between samples (y down)', arc && Math.abs(arc[1] - 150) < 1e-6 && Math.abs(arc[2] - 100) < 1e-6, JSON.stringify(arc));
  check('quad without a match in the next sample is still drawn', calls.filter(c => c[0] === 'lineTo').length === 3);
  calls.length = 0;
  drawMotion(fake, 3000);
  check('nothing drawn in a recording gap', calls.length === 0, calls.length);
  S.recFresh = true;

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
  // Art library dialog on the hand-made document.
  S.art = artLibrary({ docs: { jsb_ingamevisuals: { symbols: Object.fromEntries(Object.entries(doc.symbols).map(([k, v]) => [k, v.frames.length])) } } });
  S.art.docs.set('jsb_ingamevisuals', doc);
  openArtLibrary();
  check('art library lists the document\'s symbols', $('#artList').children.length === Object.keys(doc.symbols).length, $('#artList').children.length);
  $('#artSearch').value = 'tint'; renderArtList();
  check('art library filter', [...$('#artList').children].map(r => r.dataset.n).join() === 'NESTTINT,TINT', [...$('#artList').children].map(r => r.dataset.n).join());
  $('#artList').children[1].click();
  check('art library picks a symbol', AL.symbol === 'TINT');
  drawArtLibrary(); drawArtLibrary();
  check('art library draws', true);
  window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
  check('Esc closes the art library', !$('#artLib').classList.contains('open'));
  $('#artSearch').value = ''; AL.bundle = null; AL.symbol = null;
  // Recorded motion drawn with exported art; the collision circle places it exactly.
  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: { '1': 'COLSYM', '2': 'ROT' }, samples: [
    { t: 1000, c: [1, 100, 100, 20], q: [2, 0, 0, 100, 0, 100, 40, 0, 40] },
  ] }, 0);
  calls.length = 0;
  drawMotion(fake, 1000);
  const tf = calls.filter(c => c[0] === 'transform').map(c => c.slice(1));
  check('exported art drawn for tagged tracks', calls.filter(c => c[0] === 'drawImage').length === 2, calls.filter(c => c[0] === 'drawImage').length);
  check('art placed by its collision circle (r 10 -> 20, centre (3,4) on the hitbox)', tf.some(m => JSON.stringify(m) === '[2,0,0,2,94,92]'), JSON.stringify(tf));
  check('no outlines when every track has art', !calls.some(c => c[0] === 'arc' || c[0] === 'lineTo'));
  // Untagged small circles: round-spike art in a level with round spikes (Fox has them).
  doc.symbols.ENEMY_ROUND_SPIKES = { frames: [[{ s: 0, r: [-10, -10, 20, 20] }]] };
  S.art.where.set('ENEMY_ROUND_SPIKES', ['jsb_ingamevisuals']);
  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: {}, samples: [
    { t: 1000, c: [5, 300, 300, 10, 6, 500, 500, 90], q: [] }] }, 0);
  calls.length = 0;
  drawMotion(fake, 1000);
  check('untagged small circle uses round-spike art', calls.filter(c => c[0] === 'drawImage').length === 1);
  check('untagged big circle stays a hitbox', calls.some(c => c[0] === 'arc' && c[1] === 500));
  $('#showSprites').checked = false;
  calls.length = 0;
  drawMotion(fake, 1000);
  check('Art toggle off draws plain hitboxes', !calls.some(c => c[0] === 'drawImage') && calls.filter(c => c[0] === 'arc').length === 2);
  $('#showSprites').checked = true;
  // Long thin art (beams collide as a chain of circles) stays an outline on a circle.
  doc.symbols.LONG = { frames: [[{ s: 0, r: [-500, -5, 1000, 10] }]] };
  S.art.index.docs.jsb_ingamevisuals.symbols.LONG = 1;
  S.art.where.set('LONG', ['jsb_ingamevisuals']);
  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: { '1': 'LONG' }, samples: [
    { t: 1000, c: [1, 100, 100, 20], q: [] }] }, 0);
  calls.length = 0;
  drawMotion(fake, 1000);
  check('long art on a circle hitbox stays an outline', calls.some(c => c[0] === 'arc') && !calls.some(c => c[0] === 'drawImage'));
  // Recorded visuals (format 7): the game's display tree, drawn with the exported art.
  doc.symbols.WRAP = { frames: [[{ c: 'CHILD', i: 0, t: [0, 0, 1, 0.5] }, { s: 0, r: [0, 0, 2, 2] }]] };
  S.art.where.set('WRAP', ['jsb_ingamevisuals']);
  S.art.where.set('CHILD', ['jsb_ingamevisuals']);
  S.rec = indexRecording({ level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: {},
    symbols: [['WRAP', 'jsb_ingamevisuals'], ['CHILD', 'jsb_ingamevisuals'], ['TINT', 'jsb_ingamevisuals']],
    samples: [
      { t: 1000, c: [1, 100, 100, 20], q: [], v: [[7, 0, 1, 1, [100, 200, 90, 2, 2], [1, 4, 0.5, 0], [2, 1, 1, [5, 5, 0, 1, 1]]]] },
      { t: 1100, c: [], q: [], v: [[7, 0, 1, 1, [200, 200, 90, 2, 2], [1, 4, 0.5, 0]]] },
    ] }, 0);
  check('recording with visuals indexed', S.rec.hasVisuals && S.rec.symbols.length === 3);
  calls.length = 0;
  drawMotion(fake, 1050);
  const vtf = calls.filter(c => c[0] === 'transform').map(c => c.slice(1).map(v => Math.round(v * 1000) / 1000));
  check('visual root eased between samples and rotated clockwise', JSON.stringify(vtf[0]) === JSON.stringify([0, 2, -2, 0, 150, 200]), JSON.stringify(vtf[0]));
  const imgs = calls.filter(c => c[0] === 'drawImage');
  check('recorded child frame used (4 -> art frame 3)', imgs.some(c => c[2] === 3), JSON.stringify(imgs.map(c => c.slice(2))));
  check('root sprite + child sprite + clip attached at run time drawn', imgs.length === 3, imgs.length);
  check('collision shapes left out when visuals are drawn', !calls.some(c => c[0] === 'arc'));
  // Format 8: the game's own tints and the warning bands.
  check('shader tint 0.5 is neutral', JSON.stringify(shaderTint([0.5, 0.5, 0.5])) === '[1,0,0,0,1]');
  check('shader tint below 0.5 darkens', JSON.stringify(shaderTint([0.25, 0.25, 0.25])) === '[0.5,0,0,0,1]');
  check('shader tint 1 adds white', JSON.stringify(shaderTint([1, 1, 1])) === '[1,1,1,1,1]');
  const white = artSprite(doc, 0, [1, 1, 1, 1, 1]).getContext('2d').getImageData(0, 0, 1, 1).data;
  check('white flash drawn white', white[0] === 255 && white[1] === 255 && white[2] === 255 && white[3] === 255, [...white]);
  S.rec = indexRecording({ format: 8, level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: {},
    symbols: [['TINT', 'jsb_ingamevisuals']],
    samples: [{ t: 1000, c: [], q: [], z: [[0, 0, 1, 238, 0, 164, 1500]], v: [[1, 0, 1, 1, [10, 10, 0, 1, 1], { c: [1, 1, 1] }]] }] }, 0);
  calls.length = 0;
  drawMotion(fake, 1000);
  check('warning band drawn', calls.some(c => c[0] === 'fillRect' && JSON.stringify(c.slice(1)) === '[238,0,164,1500]'), JSON.stringify(calls.filter(c => c[0] === 'fillRect')));
  check('tinted clip drawn', calls.some(c => c[0] === 'drawImage'));
  // Format 9: transforms are the game's matrices.
  S.rec = indexRecording({ format: 9, level: levelName(), signature: 'vanilla', sampleMs: 50, tracks: {},
    symbols: [['WRAP', 'jsb_ingamevisuals'], ['CHILD', 'jsb_ingamevisuals']],
    samples: [
      { t: 1000, c: [], q: [], v: [[3, 0, 1, 1, [0, 1, -1, 0, 100, 200], [1, 2, 1, [1, 0, 0, 1, 5, 0]]]] },
      { t: 1100, c: [], q: [], v: [[3, 0, 1, 1, [0, 1, -1, 0, 200, 200], [1, 2, 1, [1, 0, 0, 1, 5, 0]]]] },
    ] }, 0);
  calls.length = 0;
  drawMotion(fake, 1050);
  const mtf = calls.filter(c => c[0] === 'transform').map(c => c.slice(1));
  check('format 9 root matrix used and eased', JSON.stringify(mtf[0]) === '[0,1,-1,0,150,200]', JSON.stringify(mtf[0]));
  check('format 9 kid matrix used as is', mtf.some(m => JSON.stringify(m) === '[1,0,0,1,5,0]'), JSON.stringify(mtf));
  S.rec = null;
  S.art = null;

  // Simulation: param expressions and a model.
  {
    const sp = newSpawn('RoundSpikes', 50000);
    check('expression: arithmetic', simNum('6-4/20', sp) === 5.8 && simNum('1225+60', sp) === 1285 && simNum('25+3*60', sp) === 205, simNum('6-4/20', sp));
    const rv = simNum('-30+random(60)+1*180', sp);
    check('expression: random() in range and stable', rv >= 150 && rv <= 210 && rv === simNum('-30+random(60)+1*180', sp), rv);
    check('expression: not a number', Number.isNaN(simNum('ENEMY_BEAM', sp)) && Number.isNaN(simNum('', sp)));
    Object.assign(sp.params, { size: '25', speed: '5', direction: '90', visual: 'ENEMY_ROUND_SPIKES' });
    sp.pos = { ...(sp.pos || {}), x: '640', y: '100', xUsed: true, yUsed: true, xRandomMargin: 0, yRandomMargin: 0 };
    const [o] = SIM_MODELS.RoundSpikes(sp);
    const m = o.node(o.t0 + 1000).m;
    // The game's cos table holds cos(90) as a float (-4.4e-8), so x drifts by ~1e-5.
    // It first moves one frame late (PHYS_DELAY).
    check('round spike moves speed*60 per second along direction (90 = down)', Math.abs(m[4] - 640) < 1e-3 && Math.abs(m[5] - (100 + 300 * (1 - PHYS_DELAY / 1000))) < 1e-3, JSON.stringify(m));
    check('round spike scale size/25, gone after leaving the stage', Math.abs(Math.hypot(m[0], m[1]) - 1) < 1e-9 && o.t1 > o.t0 + 1000 && o.t1 < o.t0 + 3000, o.t1 - o.t0);
    sp.params.speed = '80'; sp.params.size = '4';
    const [o2] = SIM_MODELS.RoundSpikes(sp), m2 = o2.node(o2.t0 + 100).m;
    check('round spike speed clamped to 50, size at least 10', Math.abs(m2[5] - (100 + 50 * 60 * (0.1 - PHYS_DELAY / 1000))) < 1e-3 && Math.abs(Math.hypot(m2[0], m2[1]) - 10 / 25) < 1e-9, JSON.stringify(m2));
    sp.params.speed = '5'; sp.params.size = '25';
    const before = S.level.spawns.length;
    S.level.spawns.push(sp); afterChange();
    check('simulated objects built for the level', simObjects().length > 0 && simObjects().some(x => x === o || x.t0 === o.t0));
    S.level.spawns.splice(before, 1); afterChange();
  }

  // Group effects move spawns with a PhysicComponent that draw through offsets: the whole
  // fxLineOfRoundSpikes line, a FxClashDiamond's orbit anchor, a FxTokyoSkiesDiamond.
  {
    const at = (type, name, t, params) => {
      const s = newSpawn(type, t);
      s.timelineName = name; s.warningMs = '0'; s.outroMs = '3000';
      s.pos = { x: '640', y: '360', xUsed: true, yUsed: true, xRandomMargin: 0, yRandomMargin: 0 };
      Object.assign(s.params, params);
      return s;
    };
    const T0 = 180000, hit = T0 + 500, look = hit + 1000;
    const line = at('fxLineOfRoundSpikes', 'tLine', T0, { num: '3', delayIntro: '0', size: '40', distBetween: '50', speed: '0' });
    const clash = at('FxClashDiamond', 'tClash', T0, { speedRotX: '1', speedRotY: '1', xDistance: '100', yDistance: '0', size: '50' });
    const tokyo = at('FxTokyoSkiesDiamond', 'tTokyo', T0, { rotOffset: '0', speed: '0', size: '50' });
    const eff = name => { const e = newSpawn('EffectVelocity', hit); e.warningMs = '0'; e.params.target = 'by name_' + name; e.params.vx = '2'; e.params.vy = '0'; return e; };
    const before = S.level.spawns.length;
    const pos = s => simObjects().filter(o => o.s === s && o.t0 <= look && look < o.t1).map(o => o.node(look).m[4]);
    S.level.spawns.push(line, clash, tokyo); afterChange();
    const still = [line, clash, tokyo].map(pos);
    S.level.spawns.push(eff('tLine'), eff('tClash'), eff('tTokyo')); afterChange();
    const moved = [line, clash, tokyo].map(pos);
    const want = 2 * Math.floor((look - hit) * 0.06), near = (a, b) => a.length && a.length === b.length && a.every((v, i) => Math.abs(v - b[i] - want) <= 4);
    check('velocity effect moves every square of a line of round spikes', near(moved[0], still[0]), JSON.stringify([still[0], moved[0], want]));
    check('velocity effect moves a clash diamond with its orbit', near(moved[1], still[1]), JSON.stringify([still[1], moved[1]]));
    check('velocity effect moves a tokyo skies diamond', near(moved[2], still[2]), JSON.stringify([still[2], moved[2]]));
    S.level.spawns.splice(before); afterChange();
  }

  // Auto-record: the practice song list and progress text.
  {
    check('practice songs listed once each', PRACTICE_SONGS.length === 52 && new Set(PRACTICE_SONGS).size === 52 && PRACTICE_SONGS.includes('FOX'));
    check('auto-record progress summary', autoRecSummary({ done: 2, skipped: 1, failed: 0, total: 9, current: 'FOX (hardcore)' }) === '3/9 · FOX (hardcore)');
  }

  // The open level's music from music/index.json.
  {
    const files = { 'index.json': JSON.stringify({ [levelName()]: 'mus_test.ogg' }), 'mus_test.ogg': 'OggS' };
    const dirHandle = { getFileHandle: async n => { if (!(n in files)) throw new Error('missing'); return { getFile: async () => new File([files[n]], n) }; } };
    const savedRoot = S.root;
    S.root = { getDirectoryHandle: async n => { if (n !== 'music') throw new Error('missing'); return dirHandle; } };
    S.audioName = null;
    await loadLevelMusic();
    check('level music loaded from music/index.json', S.audioName === 'mus_test.ogg' && audio.src.startsWith('blob:'), S.audioName);
    S.root = savedRoot;
    if (audio.src) URL.revokeObjectURL(audio.src);
    audio.removeAttribute('src'); S.audioName = null;
  }

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
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # results can hold any text
    levels = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_LEVELS
    level = os.path.join(levels, 'vanilla', 'MUSIC_FOX.json')
    catalog = os.path.join(levels, '_hazard-catalog.json')
    page = open(EDITOR, encoding='utf-8').read()
    page = page.replace('</body>', embed('levelsrc', level) + embed('catsrc', catalog) + TESTS + '</body>')

    with tempfile.TemporaryDirectory() as tmp:
        path = os.path.join(tmp, 'editor-test.html')
        open(path, 'w', encoding='utf-8').write(page)
        dom = subprocess.run(
            [CHROME, '--headless=new', '--disable-gpu', '--no-first-run', '--user-data-dir=' + os.path.join(tmp, 'profile'), '--virtual-time-budget=4000',
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
