"""Encodes the README animation from compose.js's frames with libwebp's own animation encoder (via Pillow).

ffmpeg's libwebp_anim leaves blocky smears in the dark 3D Hive areas, because it only writes a full keyframe
rarely and lossy partial frames drift there; it also ignores -g. Here a keyframe every 3 seconds keeps it clean.
Usage: python3 webp.py <frames dir> <out.webp> [fps] [width] [quality]      Needs: pip install pillow
"""
import glob
import sys

from PIL import Image

src, out = sys.argv[1], sys.argv[2]
opt = [int(a) for a in sys.argv[3:6]]
fps, width, quality = opt + [10, 1600, 48][len(opt):]
files = sorted(glob.glob(src + '/*.jpg'))[::30 // fps]


def frame(f):
    im = Image.open(f).convert('RGB')
    return im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)


key = 3 * fps
frame(files[0]).save(out, save_all=True, append_images=(frame(f) for f in files[1:]), duration=1000 // fps, loop=0,
                     quality=quality, method=4, kmin=key - 1, kmax=key)
