"""docs/img/install/*.svg: simplified UI pictures for the install guide (no vendor screenshots; labels may drift
from the real apps, so keep them generic). Run after changing the install steps."""
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

# Numbered walkthroughs: diagrams, not screenshots of unverified app controls.
from html import escape

def walkthrough(filename, title, subtitle, steps, footer):
    body = '<rect x="0" y="0" width="920" height="330" rx="18" fill="#f6f8fa"/>'
    body += f'<text x="28" y="40" font-size="22" font-weight="700" fill="#1f2328">{escape(title)}</text>'
    body += f'<text x="28" y="67" font-size="14" fill="#57606a">{escape(subtitle)}</text>'
    for i, (heading, lines) in enumerate(steps):
        x = 28 + i * 300
        body += f'<rect x="{x}" y="100" width="264" height="174" rx="14" fill="#fff" stroke="#d0d7de"/>'
        body += f'<circle cx="{x+28}" cy="130" r="15" fill="#1a7f37"/><text x="{x+28}" y="135" text-anchor="middle" font-size="14" font-weight="700" fill="#fff">{i+1}</text>'
        body += f'<text x="{x+52}" y="136" font-size="16" font-weight="700" fill="#1f2328">{escape(heading)}</text>'
        for j, line in enumerate(lines):
            body += f'<text x="{x+18}" y="{171+j*25}" font-size="14" fill="#57606a">{escape(line)}</text>'
        if i < 2:
            body += f'<path d="M{x+273} 187h18m-7-6 7 6-7 6" fill="none" stroke="#1a7f37" stroke-width="2.5" stroke-linecap="round"/>'
    body += f'<text x="28" y="308" font-size="13" fill="#57606a">{escape(footer)}</text>'
    with open(os.path.join(out, filename), 'w', encoding='utf-8', newline='\n') as f:
        f.write('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="330" viewBox="0 0 920 330" font-family="Segoe UI,Helvetica,Arial,sans-serif">'+body+'</svg>\n')

walkthrough('marketplace-add.svg', 'A · Add marketplace',
    'Claude and ChatGPT / Codex desktop: use your app’s plugin manager.', [
    ('Add marketplace', ['Open the plugin manager.', 'GPT: Create plugin →', 'Add a marketplace']),
    ('Add repository', ['Tatsuki0123/nextra-rhinogh', 'Paste the repository name.', 'Add / sync the marketplace.']),
    ('Install Nextra', ['Open the Nextra marketplace.', 'Choose the Rhino plugin.', 'Select Install.'])],
    'Start a new session with Rhino open. App labels vary; this is a workflow diagram.')

walkthrough('plugin-upload.svg', 'B · Download and upload ZIP',
    'Choose the Claude or GPT plugin download in the README.', [
    ('Download ZIP', ['Click your app’s download.', 'Save the plugin ZIP.', 'Tools + skill are included.']),
    ('Import plugin', ['GPT: Create plugin →', 'Upload plugin archive', 'Select the downloaded ZIP.']),
    ('Finish installation', ['Complete the import.', 'Start a new session.', 'Keep Rhino open.'])],
    'Claude Desktop uses Cowork for local Rhino tools. Diagram labels may differ from your app.')

# Overview: two equally prominent options, with app-specific handling underneath.
body = '<rect width="920" height="290" rx="18" fill="#f6f8fa"/>'
body += '<text x="28" y="42" font-size="24" font-weight="700" fill="#1f2328">Choose your installation option</text>'
body += '<text x="28" y="70" font-size="14" fill="#57606a">Install in Rhino first. Then choose A or B for your AI app.</text>'
for x, letter, title, lines in [
    (28, 'A', 'Add marketplace', ['Open your app’s plugin manager.', 'Add the GitHub marketplace.', 'Select Nextra → Install.']),
    (478, 'B', 'Download ZIP', ['Download the ZIP for your app.', 'Choose Upload / Import plugin.', 'Select the ZIP and install.'])]:
    body += f'<rect x="{x}" y="98" width="414" height="160" rx="14" fill="#fff" stroke="#d0d7de"/>'
    body += f'<rect x="{x+18}" y="117" width="38" height="38" rx="10" fill="#1a7f37"/><text x="{x+37}" y="143" text-anchor="middle" font-size="22" font-weight="700" fill="#fff">{letter}</text>'
    body += f'<text x="{x+70}" y="144" font-size="21" font-weight="700" fill="#1f2328">{title}</text>'
    for j, line in enumerate(lines):
        body += f'<text x="{x+18}" y="{181+25*j}" font-size="15" fill="#57606a">{escape(line)}</text>'
with open(os.path.join(out, 'two-options.svg'), 'w', encoding='utf-8', newline='\n') as f:
    f.write('<svg xmlns="http://www.w3.org/2000/svg" width="920" height="290" viewBox="0 0 920 290" font-family="Segoe UI,Helvetica,Arial,sans-serif">'+body+'</svg>\n')
print('written to', out)
