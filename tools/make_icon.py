"""Plug-in icon from docs/img/icon-source.png (the Nextra N, transparent background, 1254 px).

Trims the empty transparent margin, squares it with even padding (background stays transparent) and writes:
  docs/img/icon.png      512 px  README, website, Yak package (tools/package.ps1 and mac/package_mac.ps1 copy it)
  docs/img/icon-256.png  256 px  .mcpb icon (tools/make_mcpb.py)
  docs/img/icon.svg      a 128 px PNG inside an SVG; plug-in manifests point at icon.svg (make_plugin.py copies it),
                         and tools/make_install_images.py draws it in the PackageManager diagram
Brand colour that matches it (plug-in manifests): #32D5A0 (the bright middle of the diagonal).
Run: python tools/make_icon.py   (then python tools/make_plugin.py and python tools/make_install_images.py)
"""
import base64, io, os
from PIL import Image

repo = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
img_dir = os.path.join(repo, "docs", "img")
src = Image.open(os.path.join(img_dir, "icon-source.png")).convert("RGBA")

# content box: pixels that are clearly visible
l, t, r, b = src.getchannel("A").point(lambda v: 255 if v > 20 else 0).getbbox()
side = max(r - l, b - t)
pad = round(side * 0.04)                       # small even margin so the glow is not cut
full = side + 2 * pad
canvas = Image.new("RGBA", (full, full), (0, 0, 0, 0))
canvas.paste(src.crop((l, t, r, b)), (pad + (side - (r - l)) // 2, pad + (side - (b - t)) // 2))

outs = {}
for name, size in (("icon.png", 512), ("icon-256.png", 256), (None, 128)):
    im = canvas.resize((size, size), Image.LANCZOS)
    if name:
        im.save(os.path.join(img_dir, name), optimize=True)
    outs[size] = im

buf = io.BytesIO()
outs[128].save(buf, "PNG", optimize=True)     # 128 px inside the SVG keeps it small; plug-in UIs show it at <= 64 px
b64 = base64.b64encode(buf.getvalue()).decode("ascii")
svg = ('<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 64 64" width="64" height="64">'
       f'<image width="64" height="64" href="data:image/png;base64,{b64}" xlink:href="data:image/png;base64,{b64}"/></svg>\n')
with open(os.path.join(img_dir, "icon.svg"), "w", encoding="utf-8", newline="\n") as f:
    f.write(svg)
print(f"content box {l},{t},{r},{b} -> square {full}px (pad {pad}); wrote icon.png 512, icon-256.png, icon.svg ({len(svg)//1024} KB)")
