"""Compare a type's simulated objects with the UNTAGGED clips of a recording.

usage: python editor/test/check_untagged.py LEVEL TYPE[,TYPE...] [--lag MS] [--worst N] [--children] [--orphans]

check_sim.py compares spawns with the clips the recorder tagged with their spawn id. Some
spawns draw nothing themselves and make separate actors the recorder doesn't tag, e.g.
FxSquareLine, FxSquareLineRounded and FxSquareLine2, whose squares are pooled
FxSquareAppearOptimized actors. This compares those: every untagged recorded clip drawing a
symbol the given types' simulated objects draw is matched to the nearest simulated object of
those types alive at the sample's time minus --lag (default 43 ms; Born Survivor, Cascade
and Milky Ways fit 10), and each recorded instance (recorded id + position, split when its
frame goes back or it's missing for 400 ms) to the simulated object at that spot whose start
is closest, for lifetimes. Pooled clips show one or two samples of frame 1-3 or 158-160 at
their previous spot when reused: those instances are counted as artifacts, not compared.
Untagged clips of other types drawing the same symbols would be matched too: pick levels
where these types are the only untagged source of their symbols.

--children: for spawns that make moving child actors with their own ModelSpawn (e.g.
TutorialWallOfSpikes' RoundSpikes, recorded with spawn id 0 and type ''): clips tagged with
an empty type count as untagged, an instance is one recorded actor id (split as above), and
it is matched to the simulated object within 24 px of it at its first sample. Recordings
before format 12 are accepted (no tags: every clip is untagged).
--orphans also takes clips tagged with a type no spawn of the level has: actors a spawn makes
in code that the recorder tags with idInSong 0 and an empty or their own class's type (the
RoundSpikes particles of FxShootingStar and FxAvalanche, FxLunarWhaleExplosion).
"""
import json, os, subprocess, sys

args = sys.argv[1:]
def opt(name, default):
    if name in args:
        i = args.index(name); v = args[i + 1]; del args[i:i + 2]; return v
    return default
lag = float(opt('--lag', 43))
worst_n = int(opt('--worst', 5))
children = '--children' in args
orphans = '--orphans' in args
args = [a for a in args if a not in ('--children', '--orphans')]
level, types = args[0], args[1]
HERE = os.path.dirname(os.path.abspath(__file__))
REC = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels', 'recordings', level + '.json')
rec_url = 'file:///' + REC.replace('\\', '/')

JS = r'''(async () => {
  const rec = await (await fetch(%(rec)s)).json();
  const KIDS = %(children)s;
  if ((rec.format || 0) < 12 && !KIDS) return { error: 'recording format ' + rec.format + ' (needs 12: untagged = no spawn id)' };
  const lag = %(lag)s, TY = new Set(%(types)s.split(',')), WORST = %(worst)s, ORPH = %(orph)s;
  const levelTypes = new Set(S.level.spawns.map(s => s.type));
  const objs = simObjects().filter(o => TY.has(o.s.type));
  const syms = rec.symbols.map(s => s[0]), mine = new Set();
  for (const o of objs) { const nd = o.node(o.t0); if (nd?.n) mine.add(nd.n); }
  const B = 250, buckets = new Map();
  for (const o of objs) for (let b = Math.floor(o.t0 / B); b <= Math.floor(o.t1 / B); b++) { if (!buckets.has(b)) buckets.set(b, []); buckets.get(b).push(o); }
  const med = a => { if (!a.length) return null; const b = [...a].sort((x, y) => x - y); return b[b.length >> 1]; };
  const per = {}, inst = new Map(), done = [];
  let clips = 0, nosim = 0;
  for (const smp of rec.samples) {
    const t = smp.t - lag;
    for (const n of smp.v || []) {
      const ex = n[n.length - 1];
      if (ex && !Array.isArray(ex) && ex.s && !(KIDS && ex.s[1] === '') && !(ORPH && !levelTypes.has(ex.s[1]))) continue;
      const sym = syms[n[1]];
      if (!mine.has(sym) || !Array.isArray(n[4])) continue;
      clips++;
      const m = n[4], x = m[4], y = m[5];
      let best = null;
      for (const o of buckets.get(Math.floor(t / B)) || []) {
        if (!(o.t0 <= t && t < o.t1)) continue;
        const nd = o.node(t);
        if (!nd?.m || nd.n !== sym) continue;
        const d = Math.hypot(nd.m[4] - x, nd.m[5] - y);
        if (!best || d < best.d) best = { d, o, sc: Math.hypot(nd.m[0], nd.m[1]), f: (nd.sim ?? nd.f - 1) + 1 };
      }
      if (!best) nosim++;
      else {
        const P = per[best.o.s.type] ??= { n: 0, good: 0, d: [], s: [], f: [] };
        P.n++; if (best.d <= 8) P.good++;
        P.d.push(best.d); P.s.push(Math.abs(Math.hypot(m[0], m[1]) / best.sc - 1)); P.f.push((n[2] || 0) - best.f);
      }
      const k = KIDS ? String(n[0]) : n[0] + '|' + Math.round(x) + '|' + Math.round(y);
      let I = inst.get(k);
      if (I && (smp.t - I.b > 400 || (n[2] || 0) < I.f1)) { done.push(I); I = null; }
      if (!I) { I = { a: smp.t, b: smp.t, x, y, sym, f0: n[2] || 0, f1: 0, n: 0 }; inst.set(k, I); }
      I.b = smp.t; I.f1 = n[2] || 0; I.n++;
    }
  }
  for (const I of inst.values()) done.push(I);   // not push(...): too many for the call stack
  const used = new Set(), sD = [], eD = [], worstEnd = [];
  let artifacts = 0, unmatched = 0;
  for (const I of done) {
    if (I.n <= 2 && (I.f0 <= 3 || I.f0 >= 158)) { artifacts++; continue; }
    let best = null;
    for (let b = Math.floor((I.a - lag - 600) / B); b <= Math.floor((I.b - lag + 600) / B); b++) for (const o of buckets.get(b) || []) {
      const at = KIDS ? Math.max(o.t0, I.a - lag) : o.t0;
      if (KIDS && !(o.t0 - 100 <= I.a - lag && at < o.t1)) continue;
      const nd = o.node(at);
      if (!nd?.m || nd.n !== I.sym || Math.hypot(nd.m[4] - I.x, nd.m[5] - I.y) > (KIDS ? 24 : 8)) continue;
      const dt = Math.abs(o.t0 + lag - I.a);
      if (!best || dt < best.dt) best = { o, dt };
    }
    if (!best) { unmatched++; continue; }
    used.add(best.o);
    const s0 = I.a - (best.o.t0 + lag), e0 = I.b - (best.o.t1 + lag);
    sD.push(s0); eD.push(e0);
    worstEnd.push([idInSong(best.o.s), best.o.s.type, I.a, I.b, best.o.t0 + lag, best.o.t1 + lag, Math.round(I.x), Math.round(I.y)]);
  }
  const t0r = rec.samples[0].t, t1r = rec.samples[rec.samples.length - 1].t, spawns = new Map();
  for (const o of objs) {
    if (!(o.t0 + lag > t0r && o.t1 + lag < t1r)) continue;
    const P = spawns.get(o.s) ?? (spawns.set(o.s, [0, 0]), spawns.get(o.s));
    P[0]++; if (used.has(o)) P[1]++;
  }
  const off = [...spawns].filter(([, P]) => P[1] < P[0] * 0.8).map(([s, P]) => [idInSong(s), s.type, Math.round(T(s)), P[1], P[0]]);
  const types = {};
  for (const [k, P] of Object.entries(per)) types[k] = { n: P.n, good: P.good, medD: med(P.d), medS: med(P.s), medF: med(P.f) };
  const abs = a => a.map(Math.abs);
  worstEnd.sort((p, q) => Math.abs(q[3] - q[5]) - Math.abs(p[3] - p[5]));
  return { syms: [...mine], clips, nosim, types, inst: done.length, artifacts, unmatched, matched: sD.length,
    medStart: med(sD), medEnd: med(eD), lateStart: abs(sD).filter(d => d > 200).length, lateEnd: abs(eD).filter(d => d > 200).length,
    worstEnd: worstEnd.slice(0, WORST), spawns: spawns.size, off: off.length, worstOff: off.slice(0, WORST) };
})()'''

expr = JS % dict(children=json.dumps(children), rec=json.dumps(rec_url), lag=json.dumps(lag), types=json.dumps(types), worst=worst_n, orph=json.dumps(orphans))
out = subprocess.run([sys.executable, os.path.join(HERE, 'eval.py'), level, expr], capture_output=True, text=True, encoding='utf-8')
line = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else ''
if not line.startswith('{'):
    sys.exit('eval failed: ' + (line or out.stderr[-2000:]))
r = json.loads(line)
if 'error' in r:
    sys.exit(r['error'])

print(f'{level} (lag {lag:g} ms): untagged clips of {", ".join(r["syms"]) or "no symbol"} against {types}')
print(f'  {r["clips"]} clips, {r["nosim"]} with no simulated object alive')
for t, T in sorted(r['types'].items()):
    print(f'  {t:22s} {T["n"]:8d} clips: {T["good"]}/{T["n"]} within 8 px (median {T["medD"]:.2f} px, '
          f'scale {T["medS"]:.1%}, frame {T["medF"]:+g})')
print(f'  lifetimes of {r["matched"]} recorded instances ({r["artifacts"]} pool-reuse artifacts, {r["unmatched"]} unmatched): '
      f'start off by median {r["medStart"] or 0:.0f} ms, end by {r["medEnd"] or 0:.0f} ms; >200 ms: start {r["lateStart"]}, end {r["lateEnd"]}')
for w in r['worstEnd']:
    if abs(w[3] - w[5]) <= 200:
        break
    print(f'      idInSong {w[0]} {w[1]} at ({w[6]}, {w[7]}): seen {w[2]:.0f}-{w[3]:.0f} ms, simulated {w[4]:.0f}-{w[5]:.0f} ms')
print(f'  {r["spawns"]} spawns, {r["off"]} with under 80% of their simulated objects seen')
for w in r['worstOff']:
    print(f'      idInSong {w[0]} {w[1]} at {w[2]} ms: {w[3]}/{w[4]} seen')
