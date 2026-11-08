"""Reference renderer for the files extract_art.py writes (no game files needed).

Draws one symbol over a range of ticks onto a contact sheet, auto-fitted to the
art's extent. Mostly a check that the export is complete and a reference for
the editor's renderer.

usage: python render_art.py <doc> <SYMBOL> [--ticks START:END:STEP] [--cell 300]
                            [--art DIR] [--out FILE.png]
       python render_art.py <doc> --list
       python render_art.py --rotated      # symbols that use rotated sprites

Playback model (Flash): a symbol at tick t shows frame play(t), advancing one
frame per tick and looping, except it holds on any frame listed in "stops".
A nested instance's tick count starts when that instance id first appears in
its parent and keeps running while the parent shows it, even if the parent is
held on one frame.
"""
import argparse
import functools
import gzip
import json
import os
import sys

from PIL import Image

sys.path.insert(0, os.path.dirname(__file__))
from extract_art import documents_dir  # noqa: E402

IDENT = (1.0, 0.0, 0.0, 1.0, 0.0, 0.0)
NO_CT = ((1.0, 1.0, 1.0), (0.0, 0.0, 0.0), 1.0)  # rgb multiplier, rgb offset (0..1), alpha


def mul(P, Q):
    a1, b1, c1, d1, x1, y1 = P
    a2, b2, c2, d2, x2, y2 = Q
    return (a1 * a2 + c1 * b2, b1 * a2 + d1 * b2, a1 * c2 + c1 * d2, b1 * c2 + d1 * d2,
            a1 * x2 + c1 * y2 + x1, b1 * x2 + d1 * y2 + y1)


def invert(M):
    a, b, c, d, x, y = M
    det = a * d - b * c
    if abs(det) < 1e-12:
        return None
    ia, ib, ic, id_ = d / det, -b / det, -c / det, a / det
    return (ia, ib, ic, id_, -(ia * x + ic * y), -(ib * x + id_ * y))


def compose_ct(parent, obj):
    """parent(child(colour)): multipliers multiply, child offset is scaled by the parent."""
    pm, po, pa = parent
    if "t" in obj:
        r, g, b, k = obj["t"]
        cm, co = (1 - k,) * 3, (r * k, g * k, b * k)
    else:
        cm, co = (1.0, 1.0, 1.0), (0.0, 0.0, 0.0)
    return (tuple(p * c for p, c in zip(pm, cm)),
            tuple(p * c + o for p, c, o in zip(pm, co, po)),
            pa * obj.get("a", 1.0))


def load_doc(art_dir, doc):
    with gzip.open(os.path.join(art_dir, f"{doc}.json.gz"), "rt", encoding="utf-8") as f:
        return json.load(f)


class Art:
    def __init__(self, art_dir, doc):
        self.doc = load_doc(art_dir, doc)
        self.symbols = self.doc["symbols"]
        self.atlas = None
        if self.doc["atlas"]:
            self.atlas = Image.open(os.path.join(art_dir, self.doc["atlas"])).convert("RGBA")
        self._sprites = {}

    # -- timing
    def play(self, name, ticks):
        sym = self.symbols[name]
        n = len(sym["frames"])
        stops = set(sym.get("stops", ()))
        f = 0
        for _ in range(ticks):
            if f in stops:
                break
            f = (f + 1) % n
        return f

    @functools.lru_cache(maxsize=None)
    def frame_at(self, name, ticks):
        return self.play(name, ticks)

    def instance_age(self, name, ticks, iid):
        """Ticks since instance `iid` of `name` appeared, as seen at `ticks`."""
        age = 0
        while ticks - age - 1 >= 0:
            prev = self.symbols[name]["frames"][self.frame_at(name, ticks - age - 1)]
            if not any(o.get("i") == iid for o in prev):
                break
            age += 1
        return age

    @functools.lru_cache(maxsize=None)
    def duration(self, name):
        """Frames until the symbol and every clip nested in it have played once."""
        sym = self.symbols[name]
        children = {o["c"] for fr in sym["frames"] for o in fr if o.get("c") in self.symbols and o["c"] != name}
        return max([len(sym["frames"])] + [self.duration(c) for c in children])

    # -- geometry
    def walk(self, name, ticks, M=IDENT, ct=NO_CT):
        """Yield (obj, matrix, colour) for every sprite drawn at `ticks`."""
        sym = self.symbols[name]
        if not sym["frames"]:
            return
        for obj in sym["frames"][self.frame_at(name, ticks)]:
            Mc = mul(M, tuple(obj.get("m", IDENT)))
            ctc = compose_ct(ct, obj)
            if "s" in obj:
                yield obj, Mc, ctc
            elif obj.get("c") in self.symbols:
                yield from self.walk(obj["c"], self.instance_age(name, ticks, obj["i"]), Mc, ctc)

    def sprite(self, idx):
        if idx not in self._sprites:
            x, y, w, h, rot = self.doc["sprites"][idx]
            img = self.atlas.crop((x, y, x + w, y + h))
            if rot == 90:
                img = img.transpose(Image.Transpose.ROTATE_270)  # 90 clockwise
            elif rot == -90:
                img = img.transpose(Image.Transpose.ROTATE_90)
            self._sprites[idx] = img
        return self._sprites[idx]

    def extent(self, name, ticks_list):
        xs, ys = [], []
        for t in ticks_list:
            for obj, M, _ in self.walk(name, t):
                ax, ay, w, h = obj["r"]
                a, b, c, d, tx, ty = M
                for x, y in ((ax, ay), (ax + w, ay), (ax, ay + h), (ax + w, ay + h)):
                    xs.append(a * x + c * y + tx)
                    ys.append(b * x + d * y + ty)
        if not xs:
            return None
        return min(xs), min(ys), max(xs), max(ys)

    def draw(self, canvas, name, ticks, view):
        if self.atlas is None:
            return
        for obj, M, (cm, co, alpha) in self.walk(name, ticks):
            img = self.sprite(obj["s"])
            ax, ay, w, h = obj["r"]
            if w <= 0 or h <= 0:
                continue
            A = (w / img.width, 0, 0, h / img.height, ax, ay)  # sprite pixels -> local
            inv = invert(mul(mul(view, M), A))
            if inv is None:
                continue
            if cm != (1.0, 1.0, 1.0) or co != (0.0, 0.0, 0.0) or alpha != 1.0:
                r, g, b, a = img.split()
                r = r.point(lambda v, m=cm[0], o=co[0]: v * m + o * 255)
                g = g.point(lambda v, m=cm[1], o=co[1]: v * m + o * 255)
                b = b.point(lambda v, m=cm[2], o=co[2]: v * m + o * 255)
                a = a.point(lambda v: v * alpha)
                img = Image.merge("RGBA", (r, g, b, a))
            layer = img.transform(canvas.size, Image.Transform.AFFINE,
                                  (inv[0], inv[2], inv[4], inv[1], inv[3], inv[5]),
                                  resample=Image.Resampling.BILINEAR)
            canvas.alpha_composite(layer)


def rotated_users(art_dir):
    with open(os.path.join(art_dir, "index.json"), encoding="utf-8") as f:
        index = json.load(f)
    for doc in index["docs"]:
        d = load_doc(art_dir, doc)
        rot = {i for i, s in enumerate(d["sprites"]) if s[4]}
        if not rot:
            continue
        users = sorted({n for n, s in d["symbols"].items() for fr in s["frames"] for o in fr if o.get("s") in rot})
        print(doc, "rotated sprites", sorted(rot), "used by", users[:8])


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("doc", nargs="?")
    ap.add_argument("symbol", nargs="?")
    ap.add_argument("--art", default=os.path.join(documents_dir(), "JSAB Levels", "art"))
    ap.add_argument("--ticks", default=None, help="START:END:STEP (default: ~10 samples over one loop)")
    ap.add_argument("--cell", type=int, default=300)
    ap.add_argument("--out")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--rotated", action="store_true")
    args = ap.parse_args()

    if args.rotated:
        return rotated_users(args.art)
    art = Art(args.art, args.doc)
    if args.list:
        for n, s in art.symbols.items():
            print(f"{n:45} frames={len(s['frames']):4} stops={s.get('stops', [])[:4]} labels={list(s.get('labels', {}))[:4]}")
        return

    n = art.duration(args.symbol)
    if args.ticks:
        start, end, step = (int(v) for v in args.ticks.split(":"))
    else:
        start, end, step = 0, n, max(1, n // 10)
    ticks = list(range(start, end, step))
    ext = art.extent(args.symbol, ticks)
    cell = args.cell
    sheet = Image.new("RGBA", (cell * len(ticks), cell + 20), (24, 24, 32, 255))
    if ext:
        x0, y0, x1, y1 = ext
        scale = 0.9 * cell / max(x1 - x0, y1 - y0, 1e-6)
        cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
        view = (scale, 0, 0, scale, cell / 2 - cx * scale, cell / 2 - cy * scale)
        for k, t in enumerate(ticks):
            canvas = Image.new("RGBA", (cell, cell), (0, 0, 0, 0))
            art.draw(canvas, args.symbol, t, view)
            sheet.alpha_composite(canvas, (k * cell, 0))
        print(f"{args.symbol}: {n} ticks to play through, extent x {x0:.0f}..{x1:.0f} y {y0:.0f}..{y1:.0f}, ticks {ticks}")
    else:
        print(f"{args.symbol}: nothing drawn at ticks {ticks}")
    out = args.out or f"{args.doc}-{args.symbol}.png"
    sheet.save(out)
    print("wrote", out)


if __name__ == "__main__":
    main()
