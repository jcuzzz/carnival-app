"""Generate the PWA placeholder icons in app/public/.

Run from anywhere:  py app/scripts/generate-icons.py

PLACEHOLDER ARTWORK ONLY — a "SP" monogram on the manifest theme colour, to be
replaced by the school's own branding before the pilot. Kept in the repo so the
set can be regenerated consistently (assessment finding P2: an empty `icons: []`
blocks the Chrome/Android install prompt entirely).

Pure standard library (zlib + struct write the PNGs, signed-distance fields draw
the glyphs) so regeneration needs no Python or npm packages.

Outputs, all consumed by vite.config.ts / index.html:
  pwa-192x192.png            manifest, purpose "any"
  pwa-512x512.png            manifest, purpose "any"
  pwa-maskable-512x512.png   manifest, purpose "maskable" (full-bleed, art
                             inside the 80% safe circle Android may crop to)
  apple-touch-icon.png       180x180, iOS home screen (iOS masks its own
                             corners, so this one is a full-bleed square)
"""

import math
import struct
import zlib
from pathlib import Path

BG = (0x12, 0x34, 0x56)  # manifest theme_color
FG = (0xFF, 0xFF, 0xFF)
OUT = Path(__file__).resolve().parent.parent / "public"


def smooth(signed: float) -> float:
    """1px-wide antialiased step: >=0.5 inside the shape, 0 outside."""
    return min(1.0, max(0.0, signed + 0.5))


def dist_segment(px, py, ax, ay, bx, by) -> float:
    dx, dy = bx - ax, by - ay
    t = 0.0 if dx == dy == 0 else ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)
    t = min(1.0, max(0.0, t))
    return math.hypot(px - (ax + t * dx), py - (ay + t * dy))


def dist_arc(px, py, cx, cy, r, excluded) -> float:
    """Distance to a circle of radius r, ignoring the excluded angle window."""
    dx, dy = px - cx, py - cy
    ang = math.degrees(math.atan2(dy, dx)) % 360.0
    lo, hi = excluded
    if lo < hi:
        inside = lo < ang < hi
    else:  # window wraps past 360
        inside = ang > lo or ang < hi
    if inside:
        return 1e9
    return abs(math.hypot(dx, dy) - r)


def glyph_s(px, py, left, top, h, w) -> float:
    """Two tangent circles: upper bowl open at the right, lower open at the left."""
    r = h / 4.0
    cx = left + r
    upper = dist_arc(px, py, cx, top + r, r, (300.0, 55.0))
    lower = dist_arc(px, py, cx, top + h - r, r, (125.0, 240.0))
    return min(upper, lower)


def glyph_p(px, py, left, top, h, w) -> float:
    """Stem plus a right-hand bowl."""
    r = h * 0.30
    stem_x = left + w / 2.0
    stem = dist_segment(px, py, stem_x, top, stem_x, top + h)
    bowl = dist_arc(px, py, stem_x, top + r, r, (90.0, 270.0))
    return min(stem, bowl)


def render(size: int, corner_ratio: float, art_ratio: float, opaque: bool) -> bytes:
    h = size * art_ratio  # glyph height
    stroke = h * 0.19
    s_w = h / 2.0 + stroke
    p_w = h * 0.30 + stroke
    gap = h * 0.16
    left = (size - (s_w + gap + p_w)) / 2.0
    top = (size - h) / 2.0
    radius = size * corner_ratio
    rows = []
    for y in range(size):
        py = y + 0.5
        row = bytearray([0])
        for x in range(size):
            px = x + 0.5
            # Rounded-square background (corner_ratio 0 => plain square).
            if radius > 0:
                qx = max(radius - px, px - (size - radius), 0.0)
                qy = max(radius - py, py - (size - radius), 0.0)
                bg_a = smooth(radius - math.hypot(qx, qy)) if (qx or qy) else 1.0
            else:
                bg_a = 1.0
            d = min(
                glyph_s(px, py, left, top, h, s_w),
                glyph_p(px, py, left + s_w + gap, top, h, p_w),
            )
            fg_a = smooth(stroke / 2.0 - d) * bg_a
            alpha = 1.0 if opaque else bg_a
            row += bytes(
                round(BG[i] * (1 - fg_a) + FG[i] * fg_a) for i in range(3)
            ) + bytes([round(alpha * 255)])
        rows.append(bytes(row))
    return png(size, size, b"".join(rows))


def png(width: int, height: int, raw: bytes) -> bytes:
    def chunk(tag: bytes, data: bytes) -> bytes:
        return (
            struct.pack(">I", len(data))
            + tag
            + data
            + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
        )

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(raw, 9))
        + chunk(b"IEND", b"")
    )


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    targets = [
        ("pwa-192x192.png", 192, 0.22, 0.42, False),
        ("pwa-512x512.png", 512, 0.22, 0.42, False),
        ("pwa-maskable-512x512.png", 512, 0.0, 0.32, True),
        ("apple-touch-icon.png", 180, 0.0, 0.42, True),
    ]
    for name, size, corner, art, opaque in targets:
        (OUT / name).write_bytes(render(size, corner, art, opaque))
        print(f"wrote {name} ({size}x{size})")


if __name__ == "__main__":
    main()
