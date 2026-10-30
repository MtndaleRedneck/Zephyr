"""Subtitle position measurement for the reference frames (CI only).

measure.py <screen.png> <out-prefix> [--clip-rgb 3366AA] [--fb-bottom ROW]
Finds the video picture by the clip's solid colour (the reference clips are one flat
colour with a white all-caps subtitle line, no descenders, so the lowest white row is
the text baseline), then the subtitle text inside the picture. Prints one JSON object
and writes <out-prefix>-picture.png (the picture only) and <out-prefix>-marked.png
(picture with the baseline and the picture bottom marked).
--fb-bottom ROW: the last screen row of the video framebuffer (the screen's last row in
full screen, the video host's last row in a window); the baseline is then also measured
to it (baseline_to_framebuffer_bottom), beside the distance to the detected coloured
picture's last row (baseline_to_picture_bottom). In full screen a dark band over the
bottom of the picture makes the two differ; neither edge is assumed."""
import json, sys
import numpy as np
from PIL import Image, ImageDraw

def runs(mask):
    out, start = [], None
    for i, v in enumerate(list(mask) + [False]):
        if v and start is None: start = i
        if not v and start is not None: out.append((start, i - 1)); start = None
    return out

def measure(path, prefix, clip=(0x33, 0x66, 0xAA), fb_bottom=None):
    im = Image.open(path).convert('RGB')
    a = np.asarray(im).astype(int)
    H, W, _ = a.shape
    near = (np.abs(a - np.array(clip)).max(axis=2) <= 40)
    rows = near.sum(axis=1)
    if rows.max() < 50:
        return {"file": path, "error": "clip colour not found"}
    rr = max(runs(rows >= 0.5 * rows.max()), key=lambda r: r[1] - r[0])
    cols = near[rr[0]:rr[1] + 1].sum(axis=0)
    cr = max(runs(cols >= 0.5 * cols.max()), key=lambda r: r[1] - r[0])
    top, bottom, left, right = rr[0], rr[1], cr[0], cr[1]
    # The subtitle can sit on the picture's last rows (or outline over them): extend
    # the picture edges by the run of rows that still match at the picture's sides.
    pic = a[top:bottom + 1, left:right + 1]
    white = (pic.min(axis=2) >= 200)
    wrows = white.sum(axis=1)
    text_rows = [i for i, c in enumerate(wrows) if c >= 2]
    res = {"file": path, "picture": [int(left), int(top), int(right), int(bottom)],
           "picture_size": [int(right - left + 1), int(bottom - top + 1)]}
    if text_rows:
        clusters, cur = [], [text_rows[0], text_rows[0]]
        for r in text_rows[1:]:
            if r - cur[1] <= 4: cur[1] = r
            else: clusters.append(cur); cur = [r, r]
        clusters.append(cur)
        clusters = [c for c in clusters if c[1] - c[0] + 1 >= 5] or clusters
        t, b = clusters[-1]
        xs = np.where(white[t:b + 1].any(axis=0))[0]
        res.update({
            "text_rows_in_picture": [int(t), int(b)],
            "text_height": int(b - t + 1),
            "text_x": [int(xs.min()), int(xs.max())],
            "baseline_to_picture_bottom": int((bottom - top) - b),
            "text_top_to_picture_bottom": int((bottom - top) - t),
            "baseline_screen_y": int(top + b),
            "text_top_screen_y": int(top + t),
            "coloured_picture_bottom_screen_y": int(bottom),
        })
        if fb_bottom is not None:
            res["framebuffer_bottom_screen_y"] = int(fb_bottom)
            res["baseline_to_framebuffer_bottom"] = int(fb_bottom - (top + b))
    else:
        res["error"] = "no subtitle text found in the picture"
    crop = im.crop((left, top, right + 1, bottom + 1))
    crop.save(prefix + '-picture.png')
    mk = crop.copy(); d = ImageDraw.Draw(mk)
    if "baseline_to_picture_bottom" in res:
        t, b = res["text_rows_in_picture"]
        d.line([(0, b), (mk.width, b)], fill=(255, 0, 0))
        d.line([(0, t), (mk.width, t)], fill=(255, 160, 0))
        d.text((8, max(0, t - 40)), "baseline %d px above picture bottom, text %d px" % (res["baseline_to_picture_bottom"], res["text_height"]), fill=(255, 255, 0))
    mk.save(prefix + '-marked.png')
    if fb_bottom is not None and "baseline_to_picture_bottom" in res:
        # The whole screen with both bottoms marked: green = detected coloured picture
        # bottom, magenta = framebuffer bottom, red = baseline.
        full = im.copy(); fd = ImageDraw.Draw(full)
        fd.line([(left, top + res["text_rows_in_picture"][1]), (right, top + res["text_rows_in_picture"][1])], fill=(255, 0, 0))
        fd.line([(left, bottom), (right, bottom)], fill=(0, 255, 0))
        fd.line([(left, fb_bottom), (right, fb_bottom)], fill=(255, 0, 255))
        fd.text((left + 8, max(0, top + res["text_rows_in_picture"][0] - 60)), "baseline %d px above coloured picture bottom (green), %d px above framebuffer bottom (magenta)" % (res["baseline_to_picture_bottom"], res["baseline_to_framebuffer_bottom"]), fill=(255, 255, 0))
        full.save(prefix + '-bottoms.png')
    return res

if __name__ == '__main__':
    clip = (0x33, 0x66, 0xAA)
    if '--clip-rgb' in sys.argv:
        h = sys.argv[sys.argv.index('--clip-rgb') + 1]; clip = tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))
    fb = int(sys.argv[sys.argv.index('--fb-bottom') + 1]) if '--fb-bottom' in sys.argv else None
    print(json.dumps(measure(sys.argv[1], sys.argv[2], clip, fb)))
