"""Regenerate launcher ICOs and preview: python assets/launcher/generate.py.

Requires Pillow and CairoSVG. SVGs in this directory are the source of truth.
"""

from io import BytesIO
from pathlib import Path
import struct

import cairosvg
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[2]
ASSETS = Path(__file__).resolve().parent
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def render(name, size):
    # Wider mark and play aperture for tray sizes; keep straight edges on the
    # 16 px grid instead of shrinking the full-size artwork indiscriminately.
    source = f"{name}-small" if size <= 24 else name
    png = cairosvg.svg2png(
        url=str(ASSETS / f"{source}.svg"),
        output_width=size * 4,
        output_height=size * 4,
    )
    with Image.open(BytesIO(png)) as image:
        # Area averaging keeps the small mark's clear outer pixel intact;
        # Lanczos can spread light fringes into the reserved background.
        resampling = Image.Resampling.BOX if size <= 24 else Image.Resampling.LANCZOS
        return image.convert("RGBA").resize((size, size), resampling)


def write_ico(name, destination):
    frames = []
    for size in SIZES:
        buffer = BytesIO()
        render(name, size).save(buffer, format="PNG")
        frames.append(buffer.getvalue())
    offset = 6 + 16 * len(SIZES)
    entries = []
    for size, frame in zip(SIZES, frames):
        entries.append(struct.pack("<BBBBHHII", size % 256, size % 256,
                                   0, 0, 1, 32, len(frame), offset))
        offset += len(frame)
    destination.write_bytes(struct.pack("<HHH", 0, 1, len(SIZES))
                            + b"".join(entries) + b"".join(frames))
    with Image.open(destination) as icon:
        assert icon.ico.sizes() == {(size, size) for size in SIZES}
        for size in SIZES:
            assert icon.ico.getimage((size, size)).size == (size, size)


write_ico("normal", ROOT / "Prism.Launcher" / "app.ico")
write_ico("mqtt-unavailable", ROOT / "Prism.Launcher" / "app-offline.ico")

preview = Image.new("RGB", (750, 440), "#F5F6FA")
draw = ImageDraw.Draw(preview)
draw.rectangle((0, 220, 750, 440), fill="#191D29")
for top, text_color in ((0, "#252A3B"), (220, "#FFFFFF")):
    for column, (name, title) in enumerate((("normal", "Normal"),
                                           ("mqtt-unavailable", "MQTT unavailable"))):
        x = 30 + column * 360
        draw.text((x, top + 16), title, fill=text_color, font_size=18)
        preview.paste(render(name, 128), (x, top + 58), render(name, 128))
        for index, size in enumerate((16, 24, 32, 48)):
            left = x + 157 + index * 42
            tile = render(name, size)
            preview.paste(tile, (left, top + 96 - size // 2), tile)
            draw.text((left, top + 135), str(size), fill=text_color, font_size=12)
preview.save(ASSETS / "preview.png")
print("Generated both ICOs with nine sizes each, validated every frame, and saved preview.png.")
