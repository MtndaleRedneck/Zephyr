"""Icon proof (2.0 P0-6): every image frame of the approved assets/icon.ico must be
embedded byte-identical in the shipped exe and the installer.

Reads the .ico directory, then each PE's RT_GROUP_ICON / RT_ICON resources, and
compares frames by (width, height, bit count, SHA-256 of the image bytes).
Writes a plain report and exits non-zero when a required frame is missing.
"""
import hashlib
import struct
import sys

import pefile


def ico_frames(path):
    data = open(path, "rb").read()
    reserved, kind, count = struct.unpack_from("<HHH", data, 0)
    assert reserved == 0 and kind == 1, "not an .ico"
    frames = []
    for i in range(count):
        w, h, colors, _, planes, bits, size, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        img = data[offset:offset + size]
        frames.append(((w or 256), (h or 256), bits, hashlib.sha256(img).hexdigest()))
    return hashlib.sha256(data).hexdigest(), frames


def pe_frames(path):
    pe = pefile.PE(path, fast_load=True)
    pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_RESOURCE"]])
    icons, groups = {}, []
    root = getattr(pe, "DIRECTORY_ENTRY_RESOURCE", None)
    if root is None:
        return []
    for entry in root.entries:
        if entry.id not in (pefile.RESOURCE_TYPE["RT_ICON"], pefile.RESOURCE_TYPE["RT_GROUP_ICON"]):
            continue
        for name in entry.directory.entries:
            for lang in name.directory.entries:
                d = lang.data.struct
                blob = pe.get_data(d.OffsetToData, d.Size)
                if entry.id == pefile.RESOURCE_TYPE["RT_ICON"]:
                    icons[name.id] = blob
                else:
                    groups.append(blob)
    frames = []
    for g in groups:
        _, _, count = struct.unpack_from("<HHH", g, 0)
        for i in range(count):
            w, h, colors, _, planes, bits, size, rid = struct.unpack_from("<BBBBHHIH", g, 6 + 14 * i)
            img = icons.get(rid, b"")
            frames.append(((w or 256), (h or 256), bits, hashlib.sha256(img).hexdigest()))
    return frames


def main():
    ico, report, targets = sys.argv[1], sys.argv[2], sys.argv[3:]
    ico_sha, want = ico_frames(ico)
    lines = [f"approved icon: {ico} sha256={ico_sha} frames={len(want)} " + ", ".join(f"{w}x{h}@{b}" for w, h, b, _ in want)]
    ok = True
    for t in targets:
        have = set(pe_frames(t))
        missing = [f for f in want if f not in have]
        verdict = "MATCH (all frames byte-identical)" if not missing else "MISSING " + ", ".join(f"{w}x{h}@{b}" for w, h, b, _ in missing)
        lines.append(f"{t}: embedded frames={len(have)} -> {verdict}")
        ok = ok and not missing
    lines.append("ICON_PROOF: " + ("PASS" if ok else "FAIL"))
    text = "\n".join(lines)
    print(text)
    open(report, "w").write(text + "\n")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
