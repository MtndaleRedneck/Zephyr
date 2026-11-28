#!/usr/bin/env python3
"""Targeted, read-only dex facts for one APK: sha256 plus, per fixed target class, whether it is DEFINED in some classes*.dex
and how many method/field references point at it. No disassembly, no full class list, no file contents beyond these counts.
Failure to read an APK is printed as a line, never raised, so it cannot mask the failure of the step that called it."""
import hashlib, struct, sys, zipfile

TARGETS = ["Lkotlin/enums/EnumEntriesKt;", "Lkotlin/enums/EnumEntries;", "Lkotlin/jvm/internal/DefaultConstructorMarker;",
           "Lkotlin/jvm/internal/Intrinsics;", "Lkotlin/LazyKt;", "Lkotlin/LazyKt__LazyKt;", "Lkotlin/LazyKt__LazyJVMKt;"]

def uleb(b, o):
    r = s = 0
    while True:
        x = b[o]; o += 1; r |= (x & 0x7f) << s; s += 7
        if not x & 0x80: return r, o

def facts(b):
    u = lambda o: struct.unpack_from('<I', b, o)[0]
    ssz, so, tsz, to = u(56), u(60), u(64), u(68)
    fsz, fo, msz, mo, csz, co = u(80), u(84), u(88), u(92), u(96), u(100)
    def st(i):
        _, p = uleb(b, u(so + 4 * i)); return b[p:b.index(b'\0', p)].decode('utf8', 'replace')
    types = [st(u(to + 4 * i)) for i in range(tsz)]
    defined = {types[u(co + 32 * i)] for i in range(csz)}
    refs = {t: 0 for t in TARGETS}
    for off, n in ((mo, msz), (fo, fsz)):
        for i in range(n):
            t = types[struct.unpack_from('<H', b, off + 8 * i)[0]]
            if t in refs: refs[t] += 1
    present = set(types)
    return {t: (t in defined, refs[t], t in present) for t in TARGETS}

for path in sys.argv[1:]:
    try:
        h = hashlib.sha256(open(path, 'rb').read()).hexdigest()
        print(f"apk={path.rsplit('/', 1)[-1]} sha256={h}")
        z = zipfile.ZipFile(path)
        for name in sorted(n for n in z.namelist() if n.startswith('classes') and n.endswith('.dex')):
            f = facts(z.read(name))
            print(f"  {name}")
            for t, (d, r, p) in f.items():
                print(f"    {t} defined={d} references={r} in_type_table={p}")
    except Exception as e:  # report, never raise
        print(f"apk={path} listing failed: {type(e).__name__}")
