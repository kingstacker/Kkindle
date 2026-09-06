"""Re-encode the website screenshots: lossless PNG shrink + WebP variants."""

import os
from PIL import Image

NAMES = ["ai", "kindle", "library", "reader"]
ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets")


def mb(path):
    return os.path.getsize(path) / 1048576


for name in NAMES:
    png = os.path.join(ROOT, f"{name}.png")
    webp = os.path.join(ROOT, f"{name}.webp")
    before = mb(png)

    original = Image.open(png).convert("RGB")
    original.save(webp, "WEBP", quality=82, method=6)
    original.save(png, "PNG", optimize=True, compress_level=9)

    # PNG re-encode must stay pixel-identical.
    assert list(Image.open(png).convert("RGB").getdata()) == list(original.getdata()), name

    print(f"{name}: png {before:.2f} -> {mb(png):.2f} MB | webp {mb(webp):.2f} MB")
