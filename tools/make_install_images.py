"""Generate simplified installation UI diagrams from the README and supplied screenshots.
Run with python tools/make_install_images.py after changing installation steps."""
import os

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
out = os.path.join(repo, "docs", "img", "install")
os.makedirs(out, exist_ok=True)

CSS = """<style>
.bg{fill:#f6f8fa}.win{fill:#fff;stroke:#d0d7de;stroke-width:1.5}.bar{fill:#eaeef2}.t{font:600 15px Segoe UI,Helvetica,Arial,sans-serif;fill:#1f2328}
.s{font:13px Segoe UI,Helvetica,Arial,sans-serif;fill:#57606a}.m{font:13px Consolas,Menlo,monospace;fill:#1f2328}.f{fill:#fff;stroke:#d0d7de}
.nav{fill:#eaeef2}.navsel{fill:#ddf4ff}.btn{fill:#1f883d}.bt{font:600 13px Segoe UI,Helvetica,Arial,sans-serif;fill:#fff}
.ring{fill:none;stroke:#d1242f;stroke-width:3;stroke-dasharray:6 4}.badge{fill:#d1242f}.bn{font:700 13px Segoe UI,Arial,sans-serif;fill:#fff}
.term{fill:#0d1117}.tt{font:13px Consolas,Menlo,monospace;fill:#e6edf3}.tg{font:13px Consolas,Menlo,monospace;fill:#3fb950}.tp{font:13px Consolas,Menlo,monospace;fill:#8b949e}
@media (prefers-color-scheme: dark){.bg{fill:#0d1117}.win{fill:#161b22;stroke:#3d444d}.bar{fill:#21262d}.t,.m{fill:#e6edf3}.s{fill:#9da7b3}
.f{fill:#0d1117;stroke:#3d444d}.nav{fill:#21262d}.navsel{fill:#1f3a5f}}
</style>"""


def svg(name, w, h, body):
    with open(os.path.join(out, name), "w", encoding="utf-8", newline="\n") as f:
        f.write(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {w} {h}" width="{w}" height="{h}">{CSS}'
                f'<rect class="bg" width="{w}" height="{h}" rx="12"/>{body}</svg>\n')


def window(x, y, w, h, title):
    return (f'<rect class="win" x="{x}" y="{y}" width="{w}" height="{h}" rx="10"/>'
            f'<path class="bar" d="M{x+1} {y+34}V{y+10}a9 9 0 0 1 9-9h{w-20}a9 9 0 0 1 9 9v{24}z"/>'
            f'<circle cx="{x+18}" cy="{y+17}" r="5" fill="#ff5f57"/><circle cx="{x+34}" cy="{y+17}" r="5" fill="#febc2e"/>'
            f'<circle cx="{x+50}" cy="{y+17}" r="5" fill="#28c840"/><text class="s" x="{x+w/2}" y="{y+22}" text-anchor="middle">{title}</text>')


def field(x, y, w, label, value, mono=True):
    return (f'<text class="s" x="{x}" y="{y}">{label}</text><rect class="f" x="{x}" y="{y+6}" width="{w}" height="30" rx="6"/>'
            f'<text class="{"m" if mono else "t"}" x="{x+10}" y="{y+26}">{value}</text>')


def button(x, y, w, label, ring=True, n=None):
    s = f'<rect class="btn" x="{x}" y="{y}" width="{w}" height="32" rx="6"/><text class="bt" x="{x+w/2}" y="{y+21}" text-anchor="middle">{label}</text>'
    if ring: s += f'<rect class="ring" x="{x-6}" y="{y-6}" width="{w+12}" height="44" rx="9"/>'
    if n: s += badge(x + w + 4, y - 8, n)
    return s


def badge(x, y, n):
    return f'<circle class="badge" cx="{x}" cy="{y}" r="11"/><text class="bn" x="{x}" y="{y+4.5}" text-anchor="middle">{n}</text>'


# 1 Rhino Package Manager
svg("rhino-package-manager.svg", 720, 250,
    window(20, 16, 680, 218, "Rhino — Package Manager")
    + field(44, 72, 300, "Search", "Nextra-RhinoGh") + badge(352, 98, 1)
    + '<rect class="f" x="44" y="128" width="632" height="76" rx="8"/>'
    + '<rect x="60" y="142" width="48" height="48" rx="10" fill="#1A7F37"/><path d="M73 180v-28l22 22v-22" fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round"/>'
    + '<text class="t" x="124" y="162">Nextra-RhinoGh</text><text class="s" x="124" y="184">by Tatsuki · AI tools for Rhino 8 + Grasshopper</text>'
    + button(560, 150, 96, "Install", n=2))

# Wide, screenshot-inspired app guides live in a separate drawing module.
from install_diagrams import generate

generate(out)
print("written to", out)
