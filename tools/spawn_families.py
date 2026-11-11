"""Spawn types grouped by game class family, weighted by how often vanilla levels use them.

usage: python tools/spawn_families.py

Reads <levels>/_spawn-classes.json (SpawnClassMap) and vanilla/*.json; groups types by the
base class just above Spawn, filters boss-only attacks, marks the ones the editor simulates
from the game's code (PORTED: keep it current). The order to port in.
"""
import json, os, glob, re, collections
D = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels')
m = json.load(open(os.path.join(D, '_spawn-classes.json')))
cnt = collections.Counter(); lv = collections.defaultdict(set)
for f in glob.glob(os.path.join(D, 'vanilla', '*.json')):
    for s in json.load(open(f, encoding='utf-8'))['spawns']:
        cnt[s['type']] += 1; lv[s['type']].add(os.path.basename(f))
tot = sum(cnt.values())
BOSS = re.compile(r'boss|spider|luikang|rde_|specter|pyramid|contra|kubbi|dan_|coolfriendssaw|trythisblackhole|cod_|onTheRun|cavern|wheel', re.I)
PORTED = {'RoundSpikes', 'Wall', 'Beam', 'BeamFromAnywhere', 'BlackHole', 'applyEaseing', 'EffectVelocity', 'EffectGrav', 'FxSquareAppearStayPreParadise', 'Square', 'ExplodiongSpikes'}
NONVISUAL = {'CheckPoint'}
fam = collections.defaultdict(lambda: {'n': 0, 'types': [], 'levels': set()})
unseen = []
for t, n in cnt.most_common():
    e = m.get(t)
    if not e:
        unseen.append((t, n)); continue
    chain = [e['class']] + e['bases']
    top = chain[-2] if len(chain) >= 2 and chain[-1] == 'Spawn' else chain[0]   # the base just above Spawn
    boss = bool(BOSS.search(t) or BOSS.search(e['class']))
    key = ('BOSS ' if boss else '') + top
    F = fam[key]; F['n'] += n; F['types'].append((t, e['class'], n, t in PORTED)); F['levels'] |= lv[t]
print(f'{len(m)} types mapped, {len(cnt)} in levels; unseen {len(unseen)}: {unseen[:12]}')
for k, F in sorted(fam.items(), key=lambda kv: -kv[1]['n']):
    if k.startswith('BOSS'):
        continue
    done = sum(n for _, _, n, p in F['types'] if p)
    print(f"{k:28s} {100*F['n']/tot:5.1f}% ported {100*done/max(F['n'],1):3.0f}% lv{len(F['levels']):3d}  " +
          ', '.join(f"{t}{'=' + c if c != t else ''}({100*n/tot:.1f}){'*' if p else ''}" for t, c, n, p in F['types'][:7]))
print('boss families skipped:', sum(F['n'] for k, F in fam.items() if k.startswith('BOSS')) * 100 // tot, '% of spawns')
