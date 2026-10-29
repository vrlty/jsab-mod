"""Sanity-check a motion recording: are shape ids continuous and motion smooth?

    python editor/test/check_recording.py [path-to-recording.json]

Reports, per recording, how far tracked shapes move between consecutive samples. With
working tracking, nearly all steps are small; ids swapping between hazards show up as
large jumps.
"""
import json
import math
import os
import sys

DEFAULT = os.path.join(os.path.expanduser('~'), 'Documents', 'JSAB Levels', 'recordings', 'MUSIC_FOX.json')


def shapes(sample):
    out = {}
    c = sample['c']
    for i in range(0, len(c), 4):
        out[('c', c[i])] = (c[i + 1], c[i + 2])
    q = sample['q']
    for i in range(0, len(q), 9):
        xs, ys = q[i + 1:i + 9:2], q[i + 2:i + 9:2]
        out[('q', q[i])] = (sum(xs) / 4, sum(ys) / 4)
    return out


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else DEFAULT
    rec = json.load(open(path, encoding='utf-8'))
    samples = rec['samples']
    print(f"{rec['level']} format {rec.get('format')} signature {rec['signature']}: {len(samples)} samples at {rec['sampleMs']:.1f} ms")
    steps = []
    for a, b in zip(samples, samples[1:]):
        if b['t'] - a['t'] > rec['sampleMs'] * 2.5:
            continue
        sa, sb = shapes(a), shapes(b)
        for k, (x, y) in sa.items():
            if k in sb:
                steps.append(math.dist((x, y), sb[k]))
    if not steps:
        print('no consecutive shapes to compare')
        return
    steps.sort()
    pct = lambda p: steps[min(len(steps) - 1, int(len(steps) * p))]
    print(f"{len(steps)} matched steps; median {pct(.5):.1f}, p95 {pct(.95):.1f}, p99 {pct(.99):.1f}, max {steps[-1]:.1f} units")
    big = sum(s > 150 for s in steps)
    print(f"steps over 150 units (likely id swaps or teleports): {big} ({100 * big / len(steps):.2f}%)")


if __name__ == '__main__':
    main()
