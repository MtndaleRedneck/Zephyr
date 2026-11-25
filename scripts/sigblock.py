#!/usr/bin/env python3
"""extract: sigblock.py extract UNSIGNED SIGNED BLOCK_OUT  (block = the APK Signing Block bytes)
   apply:   sigblock.py apply UNSIGNED BLOCK_IN SIGNED_OUT"""
import struct, sys
MAGIC = b"APK Sig Block 42"
def eocd(d):
    i = len(d) - 22
    assert i >= 0 and d[i:i + 4] == b"PK\x05\x06", "EOCD must be the last 22 bytes (an archive comment is rejected)"
    assert struct.unpack_from("<H", d, i + 20)[0] == 0, "EOCD comment present"
    assert d[i - 20:i - 16] != b"PK\x06\x07", "ZIP64 archive rejected"
    cd = struct.unpack_from("<I", d, i + 16)[0]
    assert cd != 0xFFFFFFFF and struct.unpack_from("<H", d, i + 10)[0] != 0xFFFF, "ZIP64 marker values rejected"
    return i, cd
def find_block(d):
    e, cd = eocd(d)
    assert d[cd - 16:cd] == MAGIC, "no signing block"
    size = struct.unpack_from("<Q", d, cd - 24)[0]
    start = cd - size - 8
    assert struct.unpack_from("<Q", d, start)[0] == size, "block size mismatch"
    return start, cd
V2, V3, PAD = 0x7109871a, 0xf05368c0, 0x42726577  # v3.1 (0x1b93ad61, key rotation) is deliberately rejected
def check_block(b):
    """b = optional zero padding + APK Signing Block. Validates the structure; requires a v2 or v3 pair; rejects every other pair id."""
    assert len(b) > 32 and b[-16:] == MAGIC, "signing block magic missing"
    size = struct.unpack_from("<Q", b, len(b) - 24)[0]
    start = len(b) - size - 8
    assert start >= 0 and struct.unpack_from("<Q", b, start)[0] == size, "block sizes disagree"
    assert not any(b[:start]), "padding before the block is not zeros"
    pos, end, ids = start + 8, len(b) - 24, []
    while pos < end:
        n = struct.unpack_from("<Q", b, pos)[0]
        assert n >= 4 and pos + 8 + n <= end, "bad pair length"
        ids.append(struct.unpack_from("<I", b, pos + 8)[0])
        pos += 8 + n
    assert pos == end, "pairs do not fill the block"
    assert any(i in (V2, V3) for i in ids), "no v2/v3 signature pair"
    assert set(ids) <= {V2, V3, PAD}, "unexpected pair ids %s" % sorted(hex(i) for i in set(ids))
    return ids
def apply(unsigned, block):
    check_block(block)
    e, cd = eocd(unsigned)
    assert unsigned[cd - 16:cd] != MAGIC, "input already carries a signing block"
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
