"""Render the Google Play TV banner from the original Prism vector logo."""
from io import BytesIO
from pathlib import Path
import cairosvg
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'artifacts/google-play/store-listing'
OUT.mkdir(parents=True, exist_ok=True)
scale = 3
canvas = Image.new('RGB', (1280 * scale, 720 * scale), '#282828')
source = (ROOT / 'docs/public/images/prism-logo.svg').read_text(encoding='utf-8')
source = source.replace('viewBox="0 0 432 554"', 'viewBox="80 42 272 470"')
mark = Image.open(BytesIO(cairosvg.svg2png(bytestring=source.encode(),
    output_width=208 * scale, output_height=360 * scale))).convert('RGBA')
canvas.paste(mark, (152 * scale, 180 * scale), mark)
draw = ImageDraw.Draw(canvas)
fonts = Path('C:/Windows/Fonts')
draw.text((440 * scale, 263 * scale), 'ThinkingHome', font=ImageFont.truetype(
    str(fonts / 'segoeui.ttf'), 46 * scale), fill='#ABABAB', anchor='lt')
draw.text((436 * scale, 340 * scale), 'Prism Player', font=ImageFont.truetype(
    str(fonts / 'segoeuib.ttf'), 86 * scale), fill='#E7E7E7', anchor='lt')
canvas.resize((1280, 720), Image.Resampling.LANCZOS).save(OUT / 'android-tv-banner.png')
print(OUT / 'android-tv-banner.png')
