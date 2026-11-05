#!/usr/bin/env python3
"""Generate the Kkindle DMG background (no third-party imaging modules).

The image is rendered at 2x and box-filtered down so Finder's rounded
corner strokes and the drag arrow stay smooth on Retina displays.
"""
from __future__ import annotations

import argparse
import struct
import zlib
from pathlib import Path

WIDTH = 660
HEIGHT = 420
SCALE = 2


def lerp(a: float, b: float, t: float) -> float:
    return a + (b - a) * t


def blend(dst: list[int], color: tuple[int, int, int], alpha: float) -> None:
    if alpha <= 0:
        return
    if alpha > 1:
        alpha = 1.0
    for index in range(3):
        dst[index] = round(lerp(dst[index], color[index], alpha))


def rounded_rect_sdf(x: float, y: float, cx: float, cy: float, hw: float, hh: float, r: float) -> float:
    qx = abs(x - cx) - (hw - r)
    qy = abs(y - cy) - (hh - r)
    outside = (max(qx, 0.0) ** 2 + max(qy, 0.0) ** 2) ** 0.5
    return outside + min(max(qx, qy), 0.0) - r


def segment_distance(x: float, y: float, ax: float, ay: float, bx: float, by: float) -> float:
    vx, vy = bx - ax, by - ay
    length2 = vx * vx + vy * vy
    t = 0.0 if length2 == 0 else max(0.0, min(1.0, ((x - ax) * vx + (y - ay) * vy) / length2))
    px, py = ax + vx * t, ay + vy * t
    return ((x - px) ** 2 + (y - py) ** 2) ** 0.5


def point_in_triangle(
    x: float, y: float,
    ax: float, ay: float, bx: float, by: float, cx: float, cy: float,
) -> bool:
    def sign(px: float, py: float, qx: float, qy: float, rx: float, ry: float) -> float:
        return (px - rx) * (qy - ry) - (qx - rx) * (py - ry)

    d1 = sign(x, y, ax, ay, bx, by)
    d2 = sign(x, y, bx, by, cx, cy)
    d3 = sign(x, y, cx, cy, ax, ay)
    has_negative = d1 < 0 or d2 < 0 or d3 < 0
    has_positive = d1 > 0 or d2 > 0 or d3 > 0
    return not (has_negative and has_positive)


def render() -> bytes:
    width, height = WIDTH * SCALE, HEIGHT * SCALE
    pixels: list[list[int]] = []
    for py in range(height):
        y = (py + 0.5) / SCALE
        row: list[list[int]] = []
        for px in range(width):
            x = (px + 0.5) / SCALE
            t = (x / WIDTH) * 0.55 + (y / HEIGHT) * 0.45
            color = [
                round(lerp(247, 229, t)),
                round(lerp(243, 236, t)),
                round(lerp(234, 246, t)),
            ]
            # Soft glow behind the two icon wells.
            for cx in (180.0, 480.0):
                distance = ((x - cx) ** 2 + (y - 210.0) ** 2) ** 0.5
                if distance < 96:
                    blend(color, (255, 255, 255), (1 - distance / 96) * 0.35)

            # Rounded frame.
            frame = rounded_rect_sdf(x, y, WIDTH / 2, HEIGHT / 2, WIDTH / 2 - 26, HEIGHT / 2 - 26, 26)
            if abs(frame) <= 1.1:
                blend(color, (186, 176, 158), 0.55)

            # Drag arrow between the app and Applications icons.
            if segment_distance(x, y, 318.0, 210.0, 378.0, 210.0) <= 5.5:
                blend(color, (150, 140, 122), 0.55)
            if point_in_triangle(x, y, 402.0, 210.0, 376.0, 188.0, 376.0, 232.0):
                blend(color, (150, 140, 122), 0.55)

            row.append(color)
        pixels.append(row)

    # Box-filter the 2x render down to the final size.
    final: list[list[int]] = []
    for y in range(HEIGHT):
        row: list[list[int]] = []
        for x in range(WIDTH):
            block = [
                pixels[y * SCALE + dy][x * SCALE + dx]
                for dy in range(SCALE)
                for dx in range(SCALE)
            ]
            row.append([
                round(sum(pixel[channel] for pixel in block) / len(block))
                for channel in range(3)
            ])
        final.append(row)

    raw = bytearray()
    for row in final:
        raw.append(0)
        for pixel in row:
            raw.extend(pixel)
    return bytes(raw)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    return (
        struct.pack(">I", len(payload))
        + kind
        + payload
        + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("output", nargs="?", default="scripts/macos/dmg-background.png")
    args = parser.parse_args()
    output = Path(args.output)
    raw = render()
    png = (
        b"\x89PNG\r\n\x1a\n"
        + png_chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0))
        + png_chunk(b"IDAT", zlib.compress(raw, 9))
        + png_chunk(b"IEND", b"")
    )
    output.write_bytes(png)
    print(f"wrote {output} ({len(png)} bytes)")


if __name__ == "__main__":
    main()
