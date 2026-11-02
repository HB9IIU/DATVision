"""Render DATVision's editable vector identity and Windows artwork (Pillow)."""
from pathlib import Path
import math
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / 'assets/branding'
OUT.mkdir(parents=True, exist_ok=True)
NAVY = '#0c1620'
GREEN = '#57f59f'
WHITE = '#edf5fa'
MUTED = '#9cb4c5'

SVG_MARK = '''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512">
  <title>DATVision magic-eye mark</title>
  <rect width="512" height="512" rx="96" fill="#0c1620"/>
  <circle cx="256" cy="256" r="189" fill="none" stroke="#284c47" stroke-width="3"/>
  <circle cx="256" cy="256" r="169" fill="#164c36"/>
  <circle cx="256" cy="256" r="154" fill="#57f59f"/>
  <path d="M256 256 L221.3 59 L290.7 59 Z M256 256 L221.3 453 L290.7 453 Z" fill="#0c1620"/>
  <circle cx="256" cy="256" r="91" fill="#0c1620"/>
  <circle cx="256" cy="256" r="91" fill="none" stroke="#93ffd0" stroke-width="3"/>
</svg>'''
(OUT / 'datvision-mark.svg').write_text(SVG_MARK, encoding='utf-8')


def mark(size):
    scale = 4
    canvas = Image.new('RGBA', (512 * scale, 512 * scale))
    d = ImageDraw.Draw(canvas)
    def box(radius):
        return tuple(int(v * scale) for v in (256-radius, 256-radius, 256+radius, 256+radius))
    d.rounded_rectangle((0, 0, 512*scale-1, 512*scale-1), radius=96*scale, fill=NAVY)
    d.ellipse(box(189), outline='#284c47', width=3*scale)
    d.ellipse(box(169), fill='#164c36')
    d.ellipse(box(154), fill=GREEN)
    for sign in (-1, 1):
        d.polygon([(256*scale,256*scale), (221.3*scale,(256+sign*197)*scale),
                   (290.7*scale,(256+sign*197)*scale)], fill=NAVY)
    d.ellipse(box(91), fill=NAVY, outline='#93ffd0', width=3*scale)
    return canvas.resize((size,size), Image.Resampling.LANCZOS)


icon = mark(1024)
icon.save(OUT / 'datvision-icon.png')
icon.save(OUT / 'datvision.ico', sizes=[(16,16),(20,20),(24,24),(32,32),(40,40),(48,48),(64,64),(128,128),(256,256)])


def font(size, bold=False):
    return ImageFont.truetype('C:/Windows/Fonts/seguisb.ttf' if bold else 'C:/Windows/Fonts/segoeui.ttf', size)


def wordmark(image, position, size, signature=True):
    d=ImageDraw.Draw(image)
    x,y=position
    f=font(size, True)
    d.text((x,y), 'DATV', font=f, fill=WHITE)
    advance=d.textlength('DATV',font=f)
    d.text((x+advance,y), 'ision', font=f, fill=GREEN)
    if signature:
        d.text((x+3,y+size*1.24), 'by HB9IIU', font=font(round(size*.28)), fill=MUTED)


lockup=Image.new('RGBA',(1500,420),NAVY)
lockup.alpha_composite(mark(340),(40,40))
wordmark(lockup,(435,77),176)
lockup.save(OUT/'datvision-wordmark.png')

banner=Image.new('RGBA',(1200,630),NAVY)
d=ImageDraw.Draw(banner)
# Restrained signal line beneath the identity.
d.line([(96,530),(1104,530)],fill='#24413f',width=2)
d.line([(96,530),(310,530)],fill=GREEN,width=3)
banner.alpha_composite(mark(300),(80,140))
wordmark(banner,(430,177),116)
d.text((433,395),'DATV through your RTL-SDR.',font=font(28),fill=WHITE)
d.text((98,554),'DVB-S / S2    ·    RTL-SDR    ·    by HB9IIU',font=font(20),fill=MUTED)
banner.save(OUT/'datvision-github.png')

def vector_lockup(width,height,icon_x,icon_y,icon_size,text_x,text_y,text_size):
    inner=SVG_MARK[SVG_MARK.index('<rect'):SVG_MARK.rindex('</svg>')]
    return f'''<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">
<title>DATVision by HB9IIU</title>
<rect width="{width}" height="{height}" fill="{NAVY}"/>
<svg x="{icon_x}" y="{icon_y}" width="{icon_size}" height="{icon_size}" viewBox="0 0 512 512">{inner}</svg>
<text x="{text_x}" y="{text_y}" font-family="Segoe UI,Arial,sans-serif" font-size="{text_size}" font-weight="600"><tspan fill="{WHITE}">DATV</tspan><tspan fill="{GREEN}">ision</tspan></text>
<text x="{text_x+3}" y="{text_y+text_size*.58}" font-family="Segoe UI,Arial,sans-serif" font-size="{text_size*.28}" fill="{MUTED}">by HB9IIU</text>
</svg>'''

(OUT/'datvision-wordmark.svg').write_text(vector_lockup(1500,420,40,40,340,435,249,176),encoding='utf-8')
(OUT/'README.md').write_text('''# DATVision identity

Magic-eye ring with a dark centre and two tuning wedges. The shared V links
DATV and vision: white DATV, green ision. Signature: by HB9IIU.

- Navy: #0c1620
- Phosphor green: #57f59f
- Text: #edf5fa; secondary: #9cb4c5
- Typeface: Segoe UI Semibold (system font; font files are not redistributed).

datvision-mark.svg and datvision-wordmark.svg are editable masters.
datvision.ico contains 16–256 px Windows icon sizes; PNG is 1024 px.
datvision-wordmark.png is the Credits lockup; datvision-github.png is 1200×630.
Regenerate with python scripts/windows/build-branding.py (Pillow, Windows fonts).
Artwork is part of this GPL-3.0-or-later project; see ../../LICENSE.
''',encoding='utf-8')
print(f'Branding artwork generated in {OUT}')
