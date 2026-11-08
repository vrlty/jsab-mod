"""Build a name -> RVA map for GameAssembly.dll from BepInEx's Il2CppInterop cache.

BepInEx\\interop\\MethodAddressToToken.db (written by Il2CppInterop) maps every
native method address to (metadata token, assembly index) in the generated
interop assemblies next to it. Resolving the token through those assemblies
gives a full "Namespace.Type::Method" name for each function Ghidra sees.

Usage:
  python tools/method_map.py                 # writes tools/out/method_map.tsv
  python tools/method_map.py --grep Beam     # print matching rows too

Output columns: rva (hex, relative to GameAssembly.dll image base), name.
"""
import argparse
import os
import struct
import sys

import dnfile

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\Just Shapes & Beats"
INTEROP = os.path.join(GAME, "BepInEx", "interop")


def read_7bit_string(buf, pos):
    n = shift = 0
    while True:
        b = buf[pos]
        pos += 1
        n |= (b & 0x7F) << shift
        shift += 7
        if not b & 0x80:
            break
    return buf[pos:pos + n].decode("utf-8"), pos + n


def read_db(path):
    buf = open(path, "rb").read()
    magic, version, n_asm, n_methods, data_off = struct.unpack_from("<4siiii", buf, 0)
    assert magic == b"UMTM" and version == 1, (magic, version)
    pos = 20
    assemblies = []
    for _ in range(n_asm):
        s, pos = read_7bit_string(buf, pos)
        assemblies.append(s.split(",")[0])
    assert pos <= data_off
    addrs = struct.unpack_from("<%dq" % n_methods, buf, data_off)
    vals_off = data_off + 8 * n_methods
    vals = [struct.unpack_from("<ii", buf, vals_off + 8 * i) for i in range(n_methods)]
    assert vals_off + 8 * n_methods == len(buf)
    return assemblies, addrs, vals


class Assembly:
    def __init__(self, path):
        self.pe = dnfile.dnPE(path)
        md = self.pe.net.mdtables
        self.methods = md.MethodDef.rows if md.MethodDef else []
        self.owner = {}
        types = md.TypeDef.rows if md.TypeDef else []
        nested = {}
        if md.NestedClass:
            for r in md.NestedClass.rows:
                nested[r.NestedClass.row_index] = r.EnclosingClass.row_index
        names = {}

        def tname(i):
            if i in names:
                return names[i]
            t = types[i - 1]
            n = str(t.TypeName)
            if i in nested:
                n = tname(nested[i]) + "/" + n
            elif str(t.TypeNamespace):
                n = str(t.TypeNamespace) + "." + n
            names[i] = n
            return n

        for ti, t in enumerate(types, 1):
            for ref in t.MethodList or []:
                self.owner[ref.row_index] = tname(ti)

    def name(self, token):
        assert token >> 24 == 0x06, hex(token)
        row = token & 0xFFFFFF
        m = self.methods[row - 1]
        return "%s::%s" % (self.owner.get(row, "?"), m.Name)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--grep", action="append", default=[])
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "out", "method_map.tsv"))
    args = ap.parse_args()

    assemblies, addrs, vals = read_db(os.path.join(INTEROP, "MethodAddressToToken.db"))
    loaded = {}
    rows = []
    missing = set()
    for addr, (token, ai) in zip(addrs, vals):
        an = assemblies[ai]
        if an not in loaded:
            p = os.path.join(INTEROP, an + ".dll")
            loaded[an] = Assembly(p) if os.path.exists(p) else None
            if loaded[an] is None:
                missing.add(an)
        a = loaded[an]
        name = a.name(token) if a else "%s!0x%08x" % (an, token)
        rows.append((addr, an, name))

    base = min(r[0] for r in rows)
    print("methods %d, assemblies %d, missing %s, lowest address 0x%x"
          % (len(rows), len(assemblies), sorted(missing) or "none", base), file=sys.stderr)
    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        for addr, an, name in rows:
            f.write("0x%x\t%s\t%s\n" % (addr, an, name))
    for g in args.grep:
        for addr, an, name in rows:
            if g.lower() in name.lower():
                print("0x%x\t%s" % (addr, name))


if __name__ == "__main__":
    main()
