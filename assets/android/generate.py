"""Render Prism Android branding from the shared SVG logo.

Run: python assets/android/generate.py
Requires Pillow, CairoSVG, and Segoe UI (Windows). Override PRISM_FONT_DIR
to point at a directory containing segoeui.ttf and segoeuib.ttf elsewhere.
"""

from io import BytesIO
from pathlib import Path
import os
import xml.etree.ElementTree as ET

import cairosvg
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(__file__).resolve().parents[2]
OUTPUT = Path(__file__).resolve().parent
SOURCE = ROOT / "docs/public/images/prism-logo.svg"
FONT_DIR = Path(os.environ.get("PRISM_FONT_DIR", "C:/Windows/Fonts"))
NS = {"svg": "http://www.w3.org/2000/svg"}
ANDROID = "http://schemas.android.com/apk/res/android"
DENSITIES = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}
PALETTES = {
    "Player": {"background": "#131B36", "accent": "#9DD5FA", "replace": {}},
    "Library": {
        "background": "#221833", "accent": "#DFB8FA",
        "replace": {"#3D429C": "#593A88", "#4C63F2": "#9560D9",
                    "#9DD5FA": "#DFB8FA", "#5C78F5": "#B180E9",
                    "#4656C8": "#794BC1", "#5063E1": "#8954CE"},
    },
}


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text + "\n", encoding="utf-8")


def font(size, bold=False):
    return ImageFont.truetype(str(FONT_DIR / ("segoeuib.ttf" if bold else "segoeui.ttf")), size)


def logo_svg(app):
    source = SOURCE.read_text(encoding="utf-8")
    for before, after in PALETTES[app]["replace"].items():
        source = source.replace(before, after)
    return source


def logo(app, height):
    # Crop only the SVG viewport, retaining the exact approved mark geometry.
    source = logo_svg(app).replace('viewBox="0 0 432 554"', 'viewBox="80 42 272 470"')
    png = cairosvg.svg2png(bytestring=source.encode(), output_height=height * 3,
                          output_width=round(height * 272 / 470) * 3)
    with Image.open(BytesIO(png)) as image:
        return image.convert("RGBA").resize((round(height * 272 / 470), height), Image.Resampling.LANCZOS)


def paste_logo(canvas, app, x, y, height):
    mark = logo(app, height)
    canvas.alpha_composite(mark, (x, y))


def icon(app, size):
    canvas = Image.new("RGBA", (size, size), PALETTES[app]["background"])
    height = round(size * .70)
    width = round(height * 272 / 470)
    paste_logo(canvas, app, (size - width) // 2, (size - height) // 2, height)
    return canvas


def banner(app):
    canvas = Image.new("RGBA", (640, 360), PALETTES[app]["background"])
    draw = ImageDraw.Draw(canvas)
    role_font = font(80, True)
    bounds = draw.textbbox((0, 0), app, font=role_font, anchor="lt")
    text_width, text_height = bounds[2] - bounds[0], bounds[3] - bounds[1]
    logo_height, gap = 220, 44
    logo_width = round(logo_height * 272 / 470)
    left = (640 - logo_width - gap - text_width) // 2
    paste_logo(canvas, app, left, (360 - logo_height) // 2, logo_height)
    draw.text((left + logo_width + gap, (360 - text_height) // 2),
              app, fill="#F7F8FC", font=role_font, anchor="lt")
    return canvas


def vector(app, monochrome=False):
    svg = ET.fromstring(logo_svg(app))
    shape = svg.find("svg:defs/svg:clipPath/svg:path", NS).attrib["d"]
    scale = 56 / 470
    x, y = 54 - 216 * scale, 54 - 277 * scale
    lines = [f'<vector xmlns:android="{ANDROID}" android:width="108dp" android:height="108dp"',
             '    android:viewportWidth="108" android:viewportHeight="108">',
             f'  <group android:scaleX="{scale:.8f}" android:scaleY="{scale:.8f}"',
             f'      android:translateX="{x:.8f}" android:translateY="{y:.8f}">']
    if monochrome:
        lines.append(f'    <path android:fillColor="#FFFFFF" android:fillType="evenOdd" android:pathData="{shape}"/>')
    else:
        lines.append(f'    <clip-path android:fillType="evenOdd" android:pathData="{shape}"/>')
        for path in svg.findall("svg:g/svg:path", NS):
            lines.append(f'    <path android:fillColor="{path.attrib["fill"]}" android:pathData="{path.attrib["d"]}"/>')
    return "\n".join(lines + ["  </group>", "</vector>"])


for app in PALETTES:
    res = ROOT / f"Prism.{app}.Android/app/src/main/res"
    write(OUTPUT / f"{app.lower()}-logo.svg", logo_svg(app).strip())
    icon(app, 512).save(OUTPUT / f"{app.lower()}-icon.png")
    master = banner(app)
    master.save(OUTPUT / f"{app.lower()}-banner.png")
    for density, factor in DENSITIES.items():
        icon_dir, banner_dir = res / f"mipmap-{density}", res / f"drawable-{density}"
        icon_dir.mkdir(parents=True, exist_ok=True)
        banner_dir.mkdir(parents=True, exist_ok=True)
        icon(app, round(48 * factor)).save(icon_dir / "ic_launcher.png")
        master.resize((round(160 * factor), round(90 * factor)), Image.Resampling.LANCZOS).save(banner_dir / "banner.png")
    write(res / "drawable/ic_launcher_foreground.xml", vector(app))
    write(res / "drawable/ic_launcher_monochrome.xml", vector(app, monochrome=True))
    write(res / "values/branding.xml", '<resources>\n'
          f'  <color name="ic_launcher_background">{PALETTES[app]["background"]}</color>\n</resources>')
    for api in (26, 33):
        monochrome = '\n  <monochrome android:drawable="@drawable/ic_launcher_monochrome"/>' if api == 33 else ""
        write(res / f"mipmap-anydpi-v{api}/ic_launcher.xml",
              f'<adaptive-icon xmlns:android="{ANDROID}">\n'
              '  <background android:drawable="@color/ic_launcher_background"/>\n'
              '  <foreground android:drawable="@drawable/ic_launcher_foreground"/>'
              + monochrome + '\n</adaptive-icon>')

# Preserve the existing idle-screen resource dimensions and transparency.
startup = Image.new("RGBA", (900, 260))
startup_height = 210
startup_width = round(startup_height * 272 / 470)
paste_logo(startup, "Player", (900 - startup_width) // 2,
           (260 - startup_height) // 2, startup_height)
startup.save(ROOT / "Prism.Player.Android/app/src/main/res/drawable-nodpi/main_screen.png")
startup.save(OUTPUT / "player-startup.png")

# Preview includes native banners and both square/circular launcher crops.
preview = Image.new("RGB", (1040, 810), "#F3F4F8")
draw = ImageDraw.Draw(preview)
for column, app in enumerate(PALETTES):
    x = 32 + column * 512
    draw.text((x, 22), f"Prism {app}", fill="#252A3B", font=font(24, True))
    square = icon(app, 112)
    preview.paste(square, (x, 70))
    circle = Image.new("L", (112, 112))
    ImageDraw.Draw(circle).ellipse((0, 0, 111, 111), fill=255)
    preview.paste(square, (x + 136, 70), circle)
    preview.paste(banner(app).resize((448, 252), Image.Resampling.LANCZOS), (x, 208))
draw.rectangle((0, 490, 1040, 810), fill="#282828")
draw.text((32, 508), "Player / idle screen", fill="#C8CAD4", font=font(18))
preview.paste(startup, (70, 545), startup)
preview.save(OUTPUT / "preview.png")

# Validate generated Android XML and image dimensions without requiring an SDK.
for app in PALETTES:
    res = ROOT / f"Prism.{app}.Android/app/src/main/res"
    for file in (res / "drawable").glob("ic_launcher_*.xml"):
        ET.parse(file)
    for api in (26, 33):
        ET.parse(res / f"mipmap-anydpi-v{api}/ic_launcher.xml")
    ET.parse(res / "values/branding.xml")
    for density, factor in DENSITIES.items():
        with Image.open(res / f"mipmap-{density}/ic_launcher.png") as image:
            assert image.size == (round(48 * factor),) * 2
        with Image.open(res / f"drawable-{density}/banner.png") as image:
            assert image.size == (round(160 * factor), round(90 * factor))
print("Generated and validated icons, banners, adaptive/monochrome resources and player idle image.")
