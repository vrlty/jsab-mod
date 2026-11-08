"""Compare the editor's simulation with a recording, spawn by spawn.

usage: python editor/test/check_sim.py LEVEL [--lag MS] [--type TYPE] [--worst N]

Needs a recording of format 12 or later: each recorded hazard clip names its spawn
(idInSong, type). For every such clip in every sample, the editor's simulated objects of that
same spawn are evaluated at the sample's time (minus --lag, the recorder's delay, 43 ms by
default) and the closest one drawing the same symbol is compared: position (root translation)
and scale. Also compared per spawn: when it is first and last seen against the simulated
lifetime. Types without a simulation model are counted, not compared.
"""
import json, os, subprocess, sys

args = sys.argv[1:]
def opt(name, default):
    if name in args:
        i = args.index(name); v = args[i + 1]; del args[i:i + 2]; return v
    return default
lag = float(opt('--lag', 43))
only = opt('--type', None)
worst_n = int(opt('--worst', 5))
level = args[0]
HERE = os.path.dirname(os.path.abspath(__file__))
REC = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels', 'recordings', level + '.json')
rec_url = 'file:///' + REC.replace('\\', '/')

JS = r'''(async () => {
  const rec = await (await fetch(%(rec)s)).json();
  if ((rec.format || 0) < 12) return { error: 'recording format ' + rec.format + ' has no spawn ids (needs 12)' };
  const lag = %(lag)s, only = %(only)s;
  const byId = new Map(S.level.spawns.map(s => [idInSong(s), s]));
  const objs = simObjects(), bySpawn = new Map();
  for (const o of objs) { if (!bySpawn.has(o.s)) bySpawn.set(o.s, []); bySpawn.get(o.s).push(o); }
  const syms = rec.symbols.map(s => s[0]);
  const rows = [], life = new Map();
  for (const smp of rec.samples) {
    for (const n of smp.v || []) {
      const ex = n[n.length - 1];
      if (!ex || Array.isArray(ex) || !ex.s || !Array.isArray(n[4])) continue;
      const [id, type] = ex.s;
      if (only && type !== only) continue;
      const s = byId.get(id), m = n[4], sym = syms[n[1]];
      const key = id + '|' + type;
      if (!life.has(key)) life.set(key, [smp.t, smp.t]); else life.get(key)[1] = smp.t;
      if (!s || s.type !== type) { rows.push([smp.t, id, type, 'nospawn']); continue; }
      const os = bySpawn.get(s);
      if (!os) { rows.push([smp.t, id, type, 'nomodel']); continue; }
      const t = smp.t - lag, x = m[4], y = m[5], sc = Math.hypot(m[0], m[1]);
      let best = null;
      for (const o of os) {
        if (!(o.t0 <= t && t < o.t1)) continue;
        const nd = o.node(t);
        if (!nd?.m || nd.n !== sym) continue;
        const d = Math.hypot(nd.m[4] - x, nd.m[5] - y);
        if (!best || d < best[0]) best = [d, Math.hypot(nd.m[0], nd.m[1])];
      }
      rows.push(best ? [smp.t, id, type, 'ok', best[0], sc > 1e-6 ? best[1] / sc - 1 : 0] : [smp.t, id, type, 'nosim']);
    }
  }
  const lives = [];
  for (const [key, [a, b]] of life) {
    const [id, type] = key.split('|'), s = byId.get(+id), os = s && s.type === type ? bySpawn.get(s) : null;
    if (!os) continue;
    lives.push([+id, type, a, b, Math.min(...os.map(o => o.t0)) + lag, Math.max(...os.map(o => o.t1)) + lag]);
  }
  // Summarised here: a large result doesn't make it back through the headless page.
  const med = a => { if (!a.length) return null; const b = [...a].sort((x, y) => x - y); return b[b.length >> 1]; };
  const types = {}, spawns = new Map();
  for (const r of rows) {
    const T = types[r[2]] ??= { total: 0, kinds: {}, d: [], s: [], good: 0 };
    T.total++; T.kinds[r[3]] = (T.kinds[r[3]] || 0) + 1;
    if (r[3] !== 'ok') continue;
    T.d.push(r[4]); T.s.push(Math.abs(r[5]));
    if (r[4] <= %(pos)s && Math.abs(r[5]) <= %(scale)s) T.good++;
    const k = r[1] + '|' + r[2];
    const sp = spawns.get(k) ?? (spawns.set(k, { id: r[1], type: r[2], d: [], t0: r[0], t1: r[0] }), spawns.get(k));
    sp.d.push(r[4]); sp.t1 = r[0];
  }
  for (const T of Object.values(types)) { T.ok = T.d.length; T.medD = med(T.d); T.medS = med(T.s); delete T.d; delete T.s; }
  const off = [...spawns.values()].map(p => [p.id, p.type, med(p.d), p.d.length, p.t0, p.t1]).filter(p => p[2] > %(pos)s);
  for (const [type, T] of Object.entries(types)) {
    const mine = off.filter(p => p[1] === type).sort((a, b) => b[2] - a[2]);
    T.off = mine.length; T.worst = mine.slice(0, %(worst)s);
  }
  const startD = lives.map(l => Math.abs(l[2] - l[4])), endD = lives.map(l => Math.abs(l[3] - l[5]));
  const lifeSum = lives.length ? { n: lives.length, medStart: med(startD), medEnd: med(endD),
    lateStart: startD.filter(d => d > 200).length, lateEnd: endD.filter(d => d > 200).length,
    worst: [...lives].sort((a, b) => Math.abs(b[3] - b[5]) - Math.abs(a[3] - a[5])).slice(0, %(worst)s) } : null;
  return { types, lives: lifeSum };
})()'''

POS, SCALE = 8.0, 0.03
expr = JS % dict(rec=json.dumps(rec_url), lag=json.dumps(lag), only=json.dumps(only), pos=POS, scale=SCALE, worst=worst_n)
out = subprocess.run([sys.executable, os.path.join(HERE, 'eval.py'), level, expr], capture_output=True, text=True, encoding='utf-8')
line = out.stdout.strip().splitlines()[-1] if out.stdout.strip() else ''
if not line.startswith('{'):
    sys.exit('eval failed: ' + (line or out.stderr[-2000:]))
res = json.loads(line)
if 'error' in res:
    sys.exit(res['error'])

print(f'{level} (lag {lag:g} ms): recorded hazard clips per spawn type')
for type_, T in sorted(res['types'].items(), key=lambda kv: -kv[1]['total']):
    other = ', '.join(f'{k} {v}' for k, v in T['kinds'].items() if k != 'ok')
    if not T['ok']:
        print(f'  {type_:30s} {T["total"]:6d} clips: not compared ({other})')
        continue
    print(f'  {type_:30s} {T["total"]:6d} clips: {T["good"]}/{T["ok"]} within {POS:g} px and {SCALE:.0%} scale '
          f'(median {T["medD"]:.1f} px, {T["medS"]:.1%}){"; " + other if other else ""}')
    # The spawns that are furthest off, by their median error.
    if T['off']:
        print(f'      {T["off"]} spawns off by more than {POS:g} px (median); worst:')
    for id_, _, med, n, a, b in T['worst']:
        print(f'      idInSong {id_}: median {med:.1f} px over {n} clips, {a:.0f}-{b:.0f} ms')
L = res['lives']
if L:
    print(f'  lifetimes of {L["n"]} spawns: start off by median {L["medStart"]:.0f} ms, '
          f'end by median {L["medEnd"]:.0f} ms; >200 ms: start {L["lateStart"]}, end {L["lateEnd"]}')
    for l in L['worst']:
        if abs(l[3] - l[5]) <= 200:
            break
        print(f'      idInSong {l[0]} {l[1]}: seen {l[2]:.0f}-{l[3]:.0f} ms, simulated {l[4]:.0f}-{l[5]:.0f} ms')
