"""Headless browser tests for the level editor.

Injects a test script into a copy of editor/index.html together with a real vanilla
level and the hazard catalog, loads it in headless Chrome and prints the results.

    python editor/test/run_tests.py [path-to-jsab-levels]
"""
import base64
import html
import io
import json
import os
import zipfile
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
  {
    // Multi-select editing: one form, shared values shown, "mixed" blank, edits go to all.
    const bw = beams.slice(0, 3);
    mutate(() => { bw[0].warningMs = '111'; bw[1].warningMs = '222'; bw[2].warningMs = '222'; bw.forEach(b => { b.params.size = '40'; }); });
    S.selected = new Set(bw);
    renderInspector();
    const q = f => document.querySelector(`#inspBody [data-f="${f}"]`);
    check('multi-select shows a shared param', q('param:size')?.value === '40', q('param:size')?.value);
    check('multi-select marks differing values mixed', q('warningMs')?.value === '' && q('warningMs')?.placeholder === 'mixed');
    const u0 = S.undo.length;
    q('warningMs').value = '333';
    q('warningMs').dispatchEvent(new Event('change', { bubbles: true }));
    q('param:size').value = '55';
    q('param:size').dispatchEvent(new Event('change', { bubbles: true }));
    check('multi-select edit applies to every selected hazard', bw.every(b => b.warningMs === '333' && b.params.size === '55'), JSON.stringify(bw.map(b => [b.warningMs, b.params.size])));
    check('one undo step per multi-select edit', S.undo.length === u0 + 2, S.undo.length - u0);
    undo();
    check('undo of a multi-select edit', S.level.spawns.filter(b => b.params.size === '55').length === 0);
    redo();
    // undo/redo rebuild the spawn objects, so look them up again.
    const mix = ['Beam', 'Wall'].map(t => S.level.spawns.find(x => x.type === t));
    mix.forEach(x => { x.params.zzOnlyOne = '1'; });
    delete mix[1].params.zzOnlyOne;
    S.selected = new Set(mix);
    renderInspector();
    check('mixed types only show shared params', !q('param:zzOnlyOne') && !!q('warningMs'));
    delete mix[0].params.zzOnlyOne;
  }

  {
    // End at playhead: outro = snapped playhead - time, per hazard; later ones untouched.
    const made = [1000, 2000, 9000].map(t => { addAt('Wall', t); return [...S.selected][0]; });
    const outro2 = made[2].outroMs;
    S.selected = new Set(made);
    renderInspector();
    S.t = 4010;   // snaps to 4000 at 120 BPM, 1/4
    document.querySelector('#inspBody [data-act="endAtPlayhead"]').click();
    check('end at playhead sets the outro, snapped', made[0].outroMs === '3000' && made[1].outroMs === '2000' && made[2].outroMs === outro2,
      JSON.stringify(made.map(s => s.outroMs)));
    S.snapCfg.on = false;
    S.selected = new Set([made[0]]);
    renderInspector();
    document.querySelector('#inspBody [data-act="endAtPlayhead"]').click();
    check('end at playhead without snap', made[0].outroMs === '3010', made[0].outroMs);
    S.snapCfg.on = true;
    // Warning from playhead: warning = time - snapped playhead, the time stays.
    S.selected = new Set(made);
    renderInspector();
    S.t = 1510;   // snaps to 1500
    document.querySelector('#inspBody [data-act="warnFromPlayhead"]').click();
    check('warning from playhead extends the warning, time unchanged', made[0].warningMs !== '' && made[1].warningMs === '500' && made[2].warningMs === '7500'
      && T(made[1]) === 2000 && T(made[2]) === 9000, JSON.stringify(made.map(s => [s.timeMs, s.warningMs])));
    // ▾ pickers: a wall's side lists every side, picking one edits the field like typing it.
    S.selected = new Set([made[1], made[2]]);
    renderInspector();
    const pick = document.querySelector('#inspBody [data-f="param:modelSide"] + select.pick');
    const opts = pick ? [...pick.options].map(o => o.value) : [];
    check('side picker lists up/down/left/right/any', ['up', 'down', 'left', 'right', 'any'].every(v => opts.includes(v)), opts.join(','));
    pick.value = 'right';
    pick.dispatchEvent(new Event('change', { bubbles: true }));
    check('picking a value applies it to the selection', made[1].params.modelSide === 'right' && made[2].params.modelSide === 'right' && made[1].pos.yUsed);
    const boolPick = document.querySelector('#inspBody [data-f="param:shadowVisible"] + select.pick');
    check('bool params get a 1/0 picker', !!boolPick && ['1', '0'].every(v => [...boolPick.options].some(o => o.value === v)));
    check('type field has a picker', document.querySelector('#inspBody [data-f="type"] + select.pick')?.options.length > 100);
    mutate(() => { S.level.spawns = S.level.spawns.filter(s => !made.includes(s)); });
    S.selected.clear();
  }
  {
    // Group lanes: + on a lane label adds an empty group lane of that type below its lanes.
    check('vanilla level has one lane per type', S.lanes.length === new Set(S.level.spawns.map(s => s.type)).size && S.lanes.every(k => !k.includes('#')));
    const clickLabel = (key, x) => {
      const r = tl.getBoundingClientRect(), y = laneY(S.lanes.indexOf(key)) + TL.LANE / 2;
      tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + x, clientY: r.top + y, button: 0, bubbles: true }));
    };
    S.scrollY = 0;
    clickLabel('Beam', TL.LABEL - 5);
    const bi = S.lanes.indexOf('Beam');
    check('+ adds an empty group lane right after its type', S.lanes[bi + 1] === 'Beam#1' && laneLabel('Beam#1') === 'Beam #2', S.lanes.slice(bi, bi + 3).join(','));
    addAt('Beam', 3000, 1);
    const g = [...S.selected][0];
    check('adding into a group lane sets its layer', g.layer === 1 && laneKey(g) === 'Beam#1');
    // Move two ordinary beams into group 3 through the inspector.
    const two = S.level.spawns.filter(s => s.type === 'Beam' && s.layer === 0).slice(0, 2);
    S.selected = new Set(two);
    renderInspector();
    const gf = document.querySelector('#inspBody [data-f="layer"]');
    check('inspector shows the group', gf?.value === '1', gf?.value);
    gf.value = '3';
    gf.dispatchEvent(new Event('change', { bubbles: true }));
    check('Group field moves hazards to another lane', two.every(s => s.layer === 2) && S.lanes.indexOf('Beam#2') === S.lanes.indexOf('Beam#1') + 1, S.lanes.slice(bi, bi + 4).join(','));
    clickLabel('Beam#2', 30);
    check('clicking a group label selects just that group', S.selected.size === 2 && two.every(s => S.selected.has(s)), S.selected.size);
    const reopened = JSON.parse(serialize().text);
    check('groups are saved in the level file', reopened.spawns.filter(s => s.type === 'Beam' && s.layer === 2).length === 2 && reopened.spawns.some(s => s.layer === 1));
    const keep = JSON.stringify(S.level.spawns);
    setLevel(reopened, 'test');
    const bj = S.lanes.indexOf('Beam');
    check('a saved level reopens with its group lanes next to the type', S.lanes[bj + 1] === 'Beam#1' && S.lanes[bj + 2] === 'Beam#2', S.lanes.slice(bj, bj + 3).join(','));
    // Back to the level as it was, groups removed.
    mutate(() => { S.level.spawns = JSON.parse(keep).map(normSpawn).filter(s => s.timeMs !== '3000' || s.layer !== 1).map(s => ({ ...s, layer: 0 })); });
    S.selected.clear();
  }
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

  // ---- song packages (.jsab)
  const b64 = s => Uint8Array.from(atob(s), c => c.charCodeAt(0));
  const pyPkg = await readPackage(new Blob([b64(document.getElementById('pkgsrc').textContent.trim())]));
  check('reads a package made by tools/make_package.py', pyPkg.meta.id === 'test-pkg' && pyPkg.meta.name === 'Test Song'
    && pyPkg.levels.normal.length === LEVEL.spawns.length && !pyPkg.levels.hard && pyPkg.music?.size === 1000, JSON.stringify(pyPkg.meta));
  const ogg = new Uint8Array(await pyPkg.music.arrayBuffer());
  check('package music bytes intact', ogg.every((v, i) => v === (i * 7) % 256));
  openPackage(pyPkg, 'packages/test-pkg.jsab');
  check('a package opens as CUSTOM_<id> with its bar', levelName() === 'CUSTOM_test-pkg' && S.pkg === pyPkg && !$('#pkgBar').hidden && $('#replaceBox').hidden);
  check('unedited package level round-trips', JSON.stringify(spawnsForSave(S.level.spawns)) === JSON.stringify(LEVEL.spawns));
  addAt('Wall', 5000);
  const realConfirm = window.confirm;
  window.confirm = () => true;
  $('#pkgDiff').click();
  check('hardcore starts as a copy of normal, edits included', S.pkg.diff === 'hard' && S.level.spawns.length === LEVEL.spawns.length + 1 && S.dirty);
  addAt('Wall', 6000); addAt('Wall', 7000);
  $('#pkgDiff').click();
  window.confirm = realConfirm;
  check('switching back keeps both levels', S.pkg.diff === 'normal' && S.level.spawns.length === LEVEL.spawns.length + 1
    && S.pkg.levels.hard.length === LEVEL.spawns.length + 3 && S.dirty);
  const setInput = (id, v) => { $(id).value = v; $(id).dispatchEvent(new Event('change')); };
  setInput('#bpm', '137.5'); setInput('#snapDiv', '3'); setInput('#bpmOffset', '42'); setInput('#audioOffset', '-15');
  syncPkgLevel();
  const pkgBlob = await packageBlob(S.pkg);
  const again = await readPackage(pkgBlob);
  check('package write/read round trip', again.meta.id === 'test-pkg' && again.meta.author === 'Tester'
    && again.levels.normal.length === LEVEL.spawns.length + 1 && again.levels.hard.length === LEVEL.spawns.length + 3 && again.music.size === 1000);
  check('package keeps BPM, snap and offsets', JSON.stringify(again.meta.editor) === '{"bpm":137.5,"snapDiv":3,"beatsPerBar":4,"beatOffset":42,"audioOffset":-15}', JSON.stringify(again.meta.editor));
  setInput('#bpm', '90'); setInput('#snapDiv', '4'); setInput('#bpmOffset', '0'); setInput('#audioOffset', '0');
  openPackage(again, 'packages/test-pkg.jsab');
  check('opening a package restores BPM, snap and offsets', $('#bpm').value === '137.5' && $('#snapDiv').value === '3' && $('#bpmOffset').value === '42'
    && $('#audioOffset').value === '-15' && S.snapCfg.bpm === 137.5 && S.snapCfg.div === 3 && S.snapCfg.offset === 42 && S.audioOffset === -15);
  let refused = '';
  try { await packageBlob({ meta: { id: 'x', name: 'x' }, levels: { normal: [] }, music: new Blob([new Uint8Array(4)]) }); } catch (e) { refused = e.message; }
  check('a package without a CheckPoint is refused', /CheckPoint/.test(refused), refused);
  check('a bad id is refused', checkPackage({ format: 1, id: 'Bad Id', remixOf: 'MUSIC_FOX', normal: { spawns: [{ type: 'CheckPoint' }] } }, false) !== null);
  check('a remix needs no music', checkPackage({ format: 1, id: 'ok', remixOf: 'MUSIC_FOX', normal: { spawns: [{ type: 'CheckPoint' }] } }, false) === null);

  // Music the mod can play, told apart by content (not the name).
  const bytesOf = (...parts) => new Blob(parts.map(p => typeof p === 'string' ? Uint8Array.from(p, c => c.charCodeAt(0)) : new Uint8Array(p)));
  const le = (n, size) => Array.from({ length: size }, (_, i) => (n >> (8 * i)) & 0xFF);
  const wav = (format, bits) => bytesOf('RIFF', le(100, 4), 'WAVE', 'fmt ', le(16, 4), le(format, 2), le(2, 2), le(44100, 4), le(176400, 4), le(4, 2), le(bits, 2), 'data', le(8, 4), new Array(8).fill(0));
  const kinds = {
    'Ogg Vorbis': [bytesOf('OggS', new Array(24).fill(0), '\u0001vorbis'), 'ogg'],
    'Ogg Opus': [bytesOf('OggS', new Array(24).fill(0), 'OpusHead'), null],
    'WAV 16-bit': [wav(1, 16), 'wav'],
    'WAV float': [wav(3, 32), 'wav'],
    'WAV ADPCM': [wav(2, 4), null],
    'MP3 with ID3': [bytesOf('ID3', [4, 0, 0, 0, 0, 0, 0]), 'mp3'],
    'MP3 frame': [bytesOf([0xFF, 0xFB, 0x90, 0x00]), 'mp3'],
    'FLAC': [bytesOf('fLaC', [0, 0, 0, 34]), null],
    'M4A': [bytesOf([0, 0, 0, 32], 'ftypM4A '), null],
  };
  const kindResults = [];
  for (const [name, [blob, want]] of Object.entries(kinds)) {
    const k = await musicKind(blob);
    if ((k.ext ?? null) !== want) kindResults.push(`${name}: ${JSON.stringify(k)}`);
  }
  check('music formats recognised by content (ogg vorbis, mp3, wav pcm/float; not opus, adpcm, flac, m4a)', kindResults.length === 0, kindResults.join('; '));
  const mp3Pkg = { meta: { id: 'mp3-song', name: 'MP3 Song' }, levels: { normal: [{ type: 'CheckPoint', timeMs: '1000' }] },
    music: bytesOf('ID3', new Array(100).fill(7)), musicFile: 'music.mp3' };
  const mp3Back = await readPackage(await packageBlob(mp3Pkg));
  check('an MP3 package keeps music.mp3', mp3Back.musicFile === 'music.mp3' && mp3Back.music.size === 103 && mp3Back.music.type === 'audio/mpeg', `${mp3Back.musicFile} ${mp3Back.music?.size}`);
  const zipped = new Uint8Array(await pkgBlob.arrayBuffer());
  let bin = '';
  for (let i = 0; i < zipped.length; i += 8192) bin += String.fromCharCode(...zipped.subarray(i, i + 8192));
  R.push('PKG ' + btoa(bin));   // checked by Python's zipfile in main()
  setLevel(JSON.parse(JSON.stringify(LEVEL)), 'test');
  check('opening a plain level leaves package mode', !S.pkg && $('#pkgBar').hidden && !$('#replaceBox').hidden);
  setInput('#bpm', '150'); setInput('#bpmOffset', '7');
  const savedPlain = JSON.parse(serialize().text);
  setInput('#bpm', '90'); setInput('#bpmOffset', '0');
  setLevel(savedPlain, 'test');
  check('a level file keeps BPM and offset', savedPlain.editor?.bpm === 150 && $('#bpm').value === '150' && S.snapCfg.offset === 7, JSON.stringify(savedPlain.editor));
  {
    // Issue checks: hazards after the last CheckPoint; in packages also exact copies and long gaps.
    const sp = (type, t, w = 0) => ({ type, timeMs: String(t), warningMs: String(w), params: {} });
    const lvl = name => ({ format: 3, name, spawns: [sp('CheckPoint', 10000), sp('CheckPoint', 90000), sp('Beam', 5000), sp('Beam', 5000), sp('Beam', 95000, 1000), sp('Beam', 91000, 2000)] });
    const has = re => S.issues.find(i => re.test(i[1]));
    setLevel(lvl('MUSIC_FOX'), 'test');
    const late = has(/after the last CheckPoint/);
    check('hazards starting after the last CheckPoint are flagged', !!late && late[0].length === 1 && T(late[0][0]) === 95000, late?.[1]);
    check('plain levels skip the package-only checks', !has(/exact cop/) && !has(/without a CheckPoint/));
    setLevel(lvl('MUSIC_WICKED'), 'test');
    check('fixed-length vanilla songs are exempt', !has(/after the last CheckPoint/));
    openPackage({ meta: { id: 'chk', name: 'Chk' }, levels: { normal: lvl('x').spawns }, music: null, dirty: false }, 'packages/chk.jsab');
    const dup = has(/exact cop/), gap = has(/without a CheckPoint/);
    check('packages flag exact copies', !!dup && dup[0].length === 1 && T(dup[0][0]) === 5000, dup?.[1]);
    check('packages flag a long stretch without a CheckPoint', !!gap && T(gap[0]) === 90000 && /80 s/.test(gap[1]), gap?.[1]);
    const li = [...document.querySelectorAll('#issues li')].find(l => /exact cop/.test(l.textContent));
    li.click();
    check('clicking a multi-hazard issue selects them all', S.selected.size === 1 && S.selected.has(dup[0][0]));
  }
  {
    // Waveform: 1 s of silence then 0.5 s loud, as an 8 kHz 16-bit WAV.
    const sr = 8000, n = 12000, dv = new DataView(new ArrayBuffer(44 + n * 2));
    const str = (o, t) => [...t].forEach((c, i) => dv.setUint8(o + i, c.charCodeAt(0)));
    str(0, 'RIFF'); dv.setUint32(4, 36 + n * 2, true); str(8, 'WAVE'); str(12, 'fmt '); dv.setUint32(16, 16, true);
    dv.setUint16(20, 1, true); dv.setUint16(22, 1, true); dv.setUint32(24, sr, true); dv.setUint32(28, sr * 2, true);
    dv.setUint16(32, 2, true); dv.setUint16(34, 16, true); str(36, 'data'); dv.setUint32(40, n * 2, true);
    for (let i = 8000; i < n; i++) dv.setInt16(44 + i * 2, i % 2 ? 16000 : -16000, true);
    await useAudio(new Blob([dv.buffer], { type: 'audio/wav' }), 'test.wav');
    const wv = S.wave;
    check('music waveform decoded in 10 ms steps', !!wv && Math.abs(wv.peak.length - 150) <= 1, wv?.peak.length);
    check('waveform is silent then loud at the right time', !!wv && wv.peak[50] < 0.01 && wv.peak[120] / wv.max > 0.9 && wv.rms[120] > 0.3,
      wv && [wv.peak[50], wv.peak[120], wv.rms[120]].join(','));
    drawTimeline();
    check('timeline draws with the waveform band', true);
  }
  {
    // Bars and beats: 120 BPM (500 ms beats), offset 100, 1/4 snap.
    const setInput = (id, v) => { $(id).value = v; $(id).dispatchEvent(new Event('change')); };
    setInput('#bpm', '120'); setInput('#snapDiv', '4'); setInput('#bpmOffset', '100'); setInput('#bpb', '4');
    const got = [100, 600, 2100, 2225, 2160, 50].map(fmtBeat);
    check('bar.beat positions in 4/4', JSON.stringify(got) === JSON.stringify(['1.1', '1.2', '2.1', '2.1 +1/4', '2.1 (off grid)', '']), JSON.stringify(got));
    setInput('#bpb', '3');
    check('beats per bar changes the bar numbering', fmtBeat(1600) === '2.1' && S.snapCfg.bpb === 3, fmtBeat(1600));
    check('beats per bar is saved with the timing', timingOwner()?.editor?.beatsPerBar === 3, JSON.stringify(timingOwner()?.editor));
    setInput('#bpb', '4'); setInput('#bpmOffset', '0');
    drawTimeline();
    check('timeline draws with bar numbers', true);
  }
  {
    // Redesigned layout: popovers, grid chip, pinned inspector head, sections, lane eye, palette.
    const chip = $('#gridChip');
    chip.click();
    check('grid chip opens its popover', !$('#popGrid').hidden);
    document.body.click();
    check('a click outside closes the popover', $('#popGrid').hidden);
    check('grid chip summarises the grid', /^♩ 120 · 4\/4 · snap 1\/4 · \+0 ms/.test(chip.textContent), chip.textContent);
    document.querySelector('[data-pop="popMore"]').click();
    $('#btnAutoRec').click();
    check('a menu item closes its menu', $('#popMore').hidden);
    $('#autoRec').classList.remove('open');
    const beam = S.level.spawns.find(x => x.type === 'Beam');
    S.selected = new Set([beam]);
    renderInspector();
    check('inspector actions sit in the pinned head', !!document.querySelector('#inspHead [data-act="delete"]') && !document.querySelector('#inspBody [data-act="delete"]'));
    document.querySelector('#inspBody details[data-sec="params"]').open = false;
    await new Promise(r => setTimeout(r, 0));   // toggle events are queued
    renderInspector();
    check('a closed inspector section stays closed', !document.querySelector('#inspBody details[data-sec="params"]').open
      && document.querySelector('#inspBody details[data-sec="timing"]').open, JSON.stringify(S.secOpen));
    document.querySelector('#inspBody details[data-sec="params"]').open = true;
    await new Promise(r => setTimeout(r, 0));
    const r = tl.getBoundingClientRect(), y = laneY(S.lanes.indexOf('Beam')) + TL.LANE / 2;
    tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + TL.LABEL - TL.PLUS - 6, clientY: r.top + y, button: 0, bubbles: true }));
    check('the lane eye hides a lane on the stage', S.hiddenLanes.has('Beam') && S.selected.has(beam));
    drawStage(); drawTimeline();
    tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + TL.LABEL - TL.PLUS - 6, clientY: r.top + y, button: 0, bubbles: true }));
    check('clicking the eye again shows it', !S.hiddenLanes.has('Beam'));
    $('#btnPalette').click();
    check('the palette collapses', $('#palette').classList.contains('shut') && getComputedStyle($('#main')).gridTemplateColumns.startsWith('34px'));
    $('#btnPalette').click();
    check('and expands again', !$('#palette').classList.contains('shut'));
    check('status bar shows level stats', /hazards · \d+ types/.test($('#levelStats').textContent), $('#levelStats').textContent);
  }
  {
    // Speed and A–B loop (grid: 120 BPM, 4/4, offset 0, so a bar is 2000 ms).
    document.querySelector('#speedSeg [data-speed="0.5"]').click();
    check('half speed slows the music, keeping its pitch', audio.playbackRate === 0.5 && audio.defaultPlaybackRate === 0.5 && audio.preservesPitch
      && document.querySelector('#speedSeg [data-speed="0.5"]').classList.contains('on'));
    document.querySelector('#speedSeg [data-speed="1"]').click();
    S.t = 2100;
    $('#btnLoop').click();
    check('turning the loop on with none set loops two bars from the bar', S.loop.on && S.loop.a === 2000 && S.loop.b === 6000 && $('#btnLoop').classList.contains('on'), JSON.stringify(S.loop));
    S.t = 7000;
    play(); const startedAt = S.t; pause();
    check('playing from outside the loop starts at A', startedAt === 2000, startedAt);
    S.t = 1010;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: '[' }));
    check('[ sets A at the playhead (snapped)', S.loop.a === 1000 && S.loop.b === 6000, JSON.stringify(S.loop));
    S.t = 4500;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: ']' }));
    check('] sets B', S.loop.a === 1000 && S.loop.b === 4500, JSON.stringify(S.loop));
    const r = tl.getBoundingClientRect();
    S.scrollT = 0; S.pxPerSec = 100;
    tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + tlX(3010), clientY: r.top + 10, button: 0, shiftKey: true, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + tlX(5240), clientY: r.top + 10 }));
    window.dispatchEvent(new MouseEvent('mouseup', {}));
    check('Shift+drag on the ruler draws a snapped loop', S.loop.on && Math.abs(S.loop.a - 3000) < 1e-6 && Math.abs(S.loop.b - 5250) < 1e-6, JSON.stringify(S.loop));
    drawTimeline();
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'l' }));
    check('L turns the loop off and keeps it', !S.loop.on && S.loop.b > S.loop.a && !$('#btnLoop').classList.contains('on'));
  }
  {
    // Metronome: clicks on the beat grid (120 BPM: every 500 ms), accented on bars (every 2000 ms).
    const realClick = metroClick, clicks = [];
    metroClick = (when, accent) => clicks.push([Math.round((when - metroCtx.currentTime) * 1000), accent]);
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'm' }));
    check('M turns the metronome on', S.metronome && $('#btnMetro').classList.contains('on') && !!metroCtx);
    S.playing = true;
    S.t = 1950; metronomeTick();
    check('the bar\'s first beat is scheduled ahead, accented', JSON.stringify(clicks) === '[[50,true]]', JSON.stringify(clicks));
    S.t = 2100; metronomeTick();
    S.t = 2400; metronomeTick();
    check('each beat clicks once', JSON.stringify(clicks) === '[[50,true],[100,false]]', JSON.stringify(clicks));
    clicks.length = 0;
    S.t = 1000; metronomeTick();
    check('a jump back (loop, seek) restarts on the right beat', JSON.stringify(clicks) === '[[0,false]]', JSON.stringify(clicks));
    clicks.length = 0;
    S.speed = 0.5; S.t = 1480; metronomeTick(); S.speed = 1;
    check('at half speed the click waits twice as long', JSON.stringify(clicks) === '[[40,false]]', JSON.stringify(clicks));
    S.playing = false;
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'm' }));
    check('M turns it off again', !S.metronome && !$('#btnMetro').classList.contains('on'));
    metroClick = realClick;
  }
  {
    // Dragging bar ends: 100 px per second (10 ms per px), 1/4 snap at 120 BPM (125 ms).
    S.scrollT = 15; S.pxPerSec = 100; S.scrollY = 0;
    addAt('Wall', 20000); const a = [...S.selected][0];
    addAt('Wall', 22000); const b = [...S.selected][0];
    mutate(() => { for (const s of [a, b]) { s.warningMs = '1000'; s.outroMs = '500'; } });
    S.selected = new Set([a, b]);
    const r = tl.getBoundingClientRect(), y = r.top + laneY(S.lanes.indexOf('Wall')) + TL.LANE / 2;
    const dragX = (x0, x1, opts = {}) => {
      tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + x0, clientY: y, button: 0, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + x1, clientY: y, ...opts }));
      window.dispatchEvent(new MouseEvent('mouseup', {}));
    };
    tl.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + tlX(19000), clientY: y, bubbles: true }));
    check('hovering a bar end shows the resize cursor', tl.style.cursor === 'ew-resize', tl.style.cursor);
    const u0 = S.undo.length;
    dragX(tlX(19000), tlX(18510));
    check('dragging the warning start lengthens the warning (snapped), time unchanged', a.warningMs === '1500' && T(a) === 20000, `${a.warningMs} ${a.timeMs}`);
    check('the other selected hazard changes by as much', b.warningMs === '1500', b.warningMs);
    dragX(tlX(20500), tlX(20760));
    check('dragging the outro end lengthens the outro (snapped)', a.outroMs === '750' && b.outroMs === '750', `${a.outroMs} ${b.outroMs}`);
    dragX(tlX(20750), tlX(20810), { altKey: true });
    // Mouse events carry whole pixels (10 ms here); snapped would be 750 or 875.
    check('Alt drags the edge freely', Math.abs(num(a.outroMs) - 810) <= 10 && num(a.outroMs) % 125 !== 0, a.outroMs);
    check('one undo step per drag', S.undo.length === u0 + 3, S.undo.length - u0);
    dragX(tlX(18500), tlX(20400));
    check('a warning can shrink to 0 but not below', a.warningMs === '0' && b.warningMs === '0', `${a.warningMs} ${b.warningMs}`);
    undo();
    const a2 = S.level.spawns.find(s => s.type === 'Wall' && T(s) === 20000);
    check('undo restores the warning', a2?.warningMs === '1500', a2?.warningMs);
    // An outro end right on another attack's marker: the attack wins, even when the end is closer.
    addAt('Wall', 21030); const c = [...S.selected][0];
    mutate(() => { a2.outroMs = '1000'; });
    S.selected.clear();
    tl.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + tlX(21000), clientY: y, bubbles: true }));
    check('no resize cursor over an attack', tl.style.cursor === '', tl.style.cursor);
    tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + tlX(21000), clientY: y, button: 0, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mouseup', {}));
    check('clicking where a bar end overlaps an attack picks the attack', S.selected.size === 1 && S.selected.has(c) && a2.outroMs === '1000');
    mutate(() => { S.level.spawns = S.level.spawns.filter(s => !(s.type === 'Wall' && [20000, 21030, 22000].includes(T(s)))); });
    S.selected.clear();
  }
  {
    // Dragging markers up/down into another group lane of their type.
    S.scrollT = 25; S.pxPerSec = 100; S.scrollY = 0;
    const add = (type, t) => { addAt(type, t); return [...S.selected][0]; };
    const a = add('Beam', 30000), b = add('Beam', 31000), w = add('Wall', 30500);
    addGroup('Beam');
    S.selected = new Set([a, b, w]);
    const r = tl.getBoundingClientRect(), yOf = key => r.top + laneY(S.lanes.indexOf(key)) + TL.LANE / 2;
    const dragTo = (t, from, to) => {
      tl.dispatchEvent(new MouseEvent('mousedown', { clientX: r.left + tlX(t), clientY: yOf(from), button: 0, bubbles: true }));
      window.dispatchEvent(new MouseEvent('mousemove', { clientX: r.left + tlX(t), clientY: yOf(to) }));
      window.dispatchEvent(new MouseEvent('mouseup', {}));
    };
    dragTo(30000, 'Beam', 'Beam#1');
    check('dragging down into a group lane moves the type\'s hazards there, times unchanged', a.layer === 1 && b.layer === 1 && T(a) === 30000 && T(b) === 31000,
      JSON.stringify([a.layer, b.layer, T(a), T(b)]));
    check('other types in the selection stay in their lane', w.layer === 0);
    check('the moved hazards draw in the group lane', S.lanes.includes('Beam#1') && laneKey(a) === 'Beam#1');
    dragTo(30000, 'Beam#1', 'Wall');
    check('dropping on another type\'s lane changes nothing', a.layer === 1 && b.layer === 1);
    undo(); undo();
    const back = S.level.spawns.filter(s => s.type === 'Beam' && (T(s) === 30000 || T(s) === 31000));
    check('undo moves them back', back.length === 2 && back.every(s => s.layer === 0), JSON.stringify(back.map(s => s.layer)));
    mutate(() => { S.level.spawns = S.level.spawns.filter(s => !((s.type === 'Beam' && (T(s) === 30000 || T(s) === 31000)) || (s.type === 'Wall' && T(s) === 30500))); });
    S.selected.clear();
  }
  {
    // Live charting: T stamps a copy of the selection at the snapped playhead.
    const n0 = S.level.spawns.length, tap = (repeat = false) => window.dispatchEvent(new KeyboardEvent('keydown', { key: 't', repeat }));
    addAt('Wall', 50000);
    const w = [...S.selected][0];
    mutate(() => { w.layer = 2; w.params.modelSide = 'left'; w.warningMs = '750'; });
    S.t = 52010; tap();
    const c1 = [...S.selected][0];
    check('T stamps a copy at the snapped playhead and selects it', c1 !== w && T(c1) === 52000 && c1.layer === 2 && c1.params.modelSide === 'left' && c1.warningMs === '750',
      JSON.stringify([c1.timeMs, c1.layer, c1.params.modelSide, c1.warningMs]));
    S.t = 52490; tap();
    const c2 = [...S.selected][0];
    check('tapping again stamps the next one', T(c2) === 52500 && S.level.spawns.length === n0 + 3);
    tap();
    check('a second tap in the same slot is ignored', S.level.spawns.length === n0 + 3);
    S.t = 53000; tap(true);
    check('a held key does not repeat', S.level.spawns.length === n0 + 3);
    S.selected = new Set([c1, c2]);
    S.t = 60000; $('#btnTap').click();
    const pat = [...S.selected].map(T).sort((x, y) => x - y);
    check('with several selected the pattern is stamped from the playhead', JSON.stringify(pat) === '[60000,60500]' && S.level.spawns.length === n0 + 5, JSON.stringify(pat));
    undo();
    check('one undo step per tap', S.level.spawns.length === n0 + 3);
    addAt('Beam', 61000);
    S.selected.clear(); S.t = 62000; tap();
    check('with nothing selected T adds the last type used', [...S.selected][0]?.type === 'Beam' && T([...S.selected][0]) === 62000);
    mutate(() => { S.level.spawns = S.level.spawns.filter(s => !((s.type === 'Wall' && T(s) >= 50000) || (s.type === 'Beam' && (T(s) === 61000 || T(s) === 62000)))); });
    check('charting test cleaned up', S.level.spawns.length === n0, S.level.spawns.length - n0);
    S.selected.clear();
  }
} catch (e) { R.push('FAIL exception: ' + e.stack); }
finally { finish(); }
})();
// Results go into the page once the tests have finished (some steps are async).
let finished = false;
function finish() {
  if (finished) return;
  finished = true;
  const d = document.createElement('pre');
  d.id = 'results';
  d.textContent = R.join('\n');
  document.body.appendChild(d);
}
setTimeout(() => { R.push('FAIL tests did not finish'); finish(); }, 3500);
</script>
'''


def embed(element_id, path):
    text = open(path, encoding='utf-8').read().replace('</', '<\\/')
    return f'<script type="application/json" id="{element_id}">{text}</script>'


def test_package(level_path):
    """A package as tools/make_package.py writes it (deflated level.json, stored music)."""
    with open(level_path, encoding='utf-8') as f:
        spawns = json.load(f)['spawns']
    level = {'format': 1, 'id': 'test-pkg', 'name': 'Test Song', 'artist': 'Artist', 'author': 'Tester',
             'difficulty': 2, 'normal': {'spawns': spawns}}
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, 'w', zipfile.ZIP_DEFLATED) as z:
        z.writestr('level.json', json.dumps(level, indent=1))
        z.writestr('music.ogg', bytes((i * 7) % 256 for i in range(1000)), compress_type=zipfile.ZIP_STORED)
    return f'<script type="text/plain" id="pkgsrc">{base64.b64encode(buf.getvalue()).decode()}</script>'


def check_editor_package(b64, spawn_count):
    """Opens the package the editor wrote with Python's zipfile: the mod reads it with .NET's."""
    try:
        with zipfile.ZipFile(io.BytesIO(base64.b64decode(b64))) as z:
            bad = z.testzip()
            level = json.loads(z.read('level.json'))
            music = z.read('music.ogg')
            ok = (bad is None and sorted(z.namelist()) == ['level.json', 'music.ogg'] and level['format'] == 1
                  and level['id'] == 'test-pkg' and len(level['normal']['spawns']) == spawn_count + 1
                  and len(level['hard']['spawns']) == spawn_count + 3 and music == bytes((i * 7) % 256 for i in range(1000)))
            return ('PASS' if ok else 'FAIL') + ' the editor\'s package opens in Python\'s zipfile with the right contents'
    except Exception as e:
        return f'FAIL the editor\'s package opens in Python\'s zipfile :: {e}'


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # results can hold any text
    levels = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_LEVELS
    level = os.path.join(levels, 'vanilla', 'MUSIC_FOX.json')
    catalog = os.path.join(levels, '_hazard-catalog.json')
    page = open(EDITOR, encoding='utf-8').read()
    page = page.replace('</body>', embed('levelsrc', level) + embed('catsrc', catalog) + test_package(level) + TESTS + '</body>')

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
    with open(level, encoding='utf-8') as f:
        spawn_count = len(json.load(f)['spawns'])
    lines = []
    for line in results.splitlines():
        lines.append(check_editor_package(line[4:], spawn_count) if line.startswith('PKG ') else line)
    results = '\n'.join(lines)
    print(results)
    failed = sum(line.startswith('FAIL') for line in results.splitlines())
    print(f'\n{failed} failed' if failed else '\nall passed')
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
