"""Build optimized website imagery.

Produces:
  * lossless PNG re-encodes and full-size WebP versions of every screenshot
  * 640w / 1024w WebP variants for responsive ``srcset`` delivery
  * a cropped library-grid screenshot used by the first feature block
  * a 1200x630 social sharing card
  * a 180x180 Apple touch icon

Requires Pillow: ``python3 -m pip install pillow``.
"""

from __future__ import annotations

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1] / "website" / "assets"

SCREENSHOTS = ["ai", "kindle", "library", "reader"]
RESPONSIVE_WIDTHS = (640, 1024)
WEBP_QUALITY = 82


def mb(path: Path) -> float:
    return path.stat().st_size / 1048576


def save_webp(image: Image.Image, path: Path) -> None:
    image.save(path, "WEBP", quality=WEBP_QUALITY, method=6)


def build_responsive(name: str, source: Image.Image) -> None:
    for width in RESPONSIVE_WIDTHS:
        if width >= source.width:
            continue
        height = round(source.height * width / source.width)
        variant = source.resize((width, height), Image.Resampling.LANCZOS)
        save_webp(variant, ROOT / f"{name}-{width}.webp")


def main() -> None:
    for name in SCREENSHOTS:
        png = ROOT / f"{name}.png"
        webp = ROOT / f"{name}.webp"
        before = mb(png)

        original = Image.open(png).convert("RGB")
        save_webp(original, webp)
        original.save(png, "PNG", optimize=True, compress_level=9)
        build_responsive(name, original)

        # The PNG re-encode must stay pixel-identical.
        assert list(Image.open(png).convert("RGB").getdata()) == list(original.getdata()), name
        print(f"{name}: png {before:.2f} -> {mb(png):.2f} MB | webp {mb(webp):.2f} MB")

    # A distinct crop for the "local library" feature, so the page does not
    # show the same full screenshot twice and the alt text ("grid view") matches.
    library = Image.open(ROOT / "library.png").convert("RGB")
    grid = library.crop((288, 0, library.width, library.height))
    grid.save(ROOT / "library-grid.png", "PNG", optimize=True, compress_level=9)
    save_webp(grid, ROOT / "library-grid.webp")
    build_responsive("library-grid", grid)
    print(f"library-grid: {grid.width}x{grid.height} | webp {mb(ROOT / 'library-grid.webp'):.2f} MB")

    # Social sharing card (1200x630) derived from the library screenshot.
    card_width, card_height = 1200, 630
    scaled_height = round(library.height * card_width / library.width)
    card = library.resize((card_width, scaled_height), Image.Resampling.LANCZOS)
    top = max(0, (scaled_height - card_height) // 2)
    card = card.crop((0, top, card_width, top + card_height))
    card.save(ROOT / "social-card.jpg", "JPEG", quality=82, optimize=True, progressive=True)
    print(f"social-card: {card.width}x{card.height} | {mb(ROOT / 'social-card.jpg'):.2f} MB")

    icon = Image.open(ROOT / "kkindle.png").convert("RGBA")
    icon.resize((180, 180), Image.Resampling.LANCZOS).save(
        ROOT / "apple-touch-icon.png", "PNG", optimize=True
    )
    print("apple-touch-icon: 180x180")


if __name__ == "__main__":
    main()
