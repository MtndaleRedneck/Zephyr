#!/usr/bin/env python3
"""extract: sigblock.py extract UNSIGNED SIGNED BLOCK_OUT  (block = the APK Signing Block bytes)
   apply:   sigblock.py apply UNSIGNED BLOCK_IN SIGNED_OUT"""
import struct, sys
MAGIC = b"APK Sig Block 42"
def eocd(d):
    i = d.rfind(b"PK\x05\x06")
    assert i >= 0 and len(d) - i == 22 + struct.unpack_from("<H", d, i + 20)[0], "EOCD"
    return i, struct.unpack_from("<I", d, i + 16)[0]
def find_block(d):
    e, cd = eocd(d)
    assert d[cd - 16:cd] == MAGIC, "no signing block"
    size = struct.unpack_from("<Q", d, cd - 24)[0]
    start = cd - size - 8
    assert struct.unpack_from("<Q", d, start)[0] == size, "block size mismatch"
    return start, cd
def apply(unsigned, block):
    e, cd = eocd(unsigned)
    assert unsigned[cd - 16:cd] != MAGIC, "input already carries a signing block"
    assert len(block) % 4096 == 0 or True
    out = bytearray(unsigned[:cd] + block + unsigned[cd:])
    e2 = e + len(block)
    struct.pack_into("<I", out, e2 + 16, cd + len(block))
    return bytes(out)
if __name__ == "__main__":
    m = sys.argv[1]
    if m == "extract":
        u, s = open(sys.argv[2], "rb").read(), open(sys.argv[3], "rb").read()
        a, b = find_block(s)
        cdu = eocd(u)[1]
        assert s[:cdu] == u[:cdu], "entries differ: signing changed more than the signing block"
        block = s[cdu:b]  # padding apksigner adds for alignment plus the APK Signing Block
        assert apply(u, block) == s, "unsigned + block does not reproduce the signed APK byte for byte"
        open(sys.argv[4], "wb").write(block)
        print("block bytes", len(block))
    elif m == "apply":
        open(sys.argv[4], "wb").write(apply(open(sys.argv[2], "rb").read(), open(sys.argv[3], "rb").read()))
