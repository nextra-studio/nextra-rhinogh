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

# 2 Claude Desktop: Customize > Plugins > + Add > Upload plugin
svg("claude-desktop-plugin.svg", 720, 280,
    window(20, 16, 680, 248, "Claude — Customize")
    + '<text class="s" x="44" y="76">Skills</text><text class="s" x="98" y="76">Connectors</text>'
    + '<rect class="navsel" x="178" y="58" width="70" height="28" rx="6"/><text class="t" x="188" y="77">Plugins</text>' + badge(254, 60, 1)
    + button(560, 56, 110, "+ Add", ring=False)
    + '<rect class="win" x="470" y="98" width="200" height="40" rx="8"/><text class="t" x="490" y="123">Upload plugin</text>'
    + '<rect class="ring" x="464" y="92" width="212" height="52" rx="10"/>' + badge(680, 96, 2)
    + '<rect class="f" x="44" y="168" width="400" height="44" rx="8"/><text class="m" x="60" y="196">nextra-rhinogh-plugin.zip</text>' + badge(452, 170, 3)
    + '<text class="s" x="44" y="248">pick the zip (do not unzip it), then Add</text>')

# 4 Claude Code / terminal
svg("claude-code.svg", 720, 170,
    '<rect class="term" x="20" y="16" width="680" height="138" rx="10"/>'
    + '<circle cx="38" cy="33" r="5" fill="#ff5f57"/><circle cx="54" cy="33" r="5" fill="#febc2e"/><circle cx="70" cy="33" r="5" fill="#28c840"/>'
    + '<text class="tp" x="40" y="68">&gt;</text><text class="tt" x="56" y="68">/plugin install nextra-rhinogh --marketplace Tatsuki0123/nextra-rhinogh</text>'
    + '<text class="tg" x="40" y="98">✔ Installed nextra-rhinogh (MCP server + skill)</text>'
    + '<text class="tp" x="40" y="126">  restart Claude Code, then ask: "Create a simple timber house in Rhino"</text>')

# 5 ChatGPT / Codex desktop: unzip, then paste one prompt
svg("chatgpt-prompt.svg", 720, 240,
    window(20, 16, 680, 208, "ChatGPT / Codex desktop — Codex or Work mode")
    + '<text class="s" x="44" y="70">After unzipping nextra-rhinogh-agent-kit.zip, paste (use your folder path):</text>' + badge(676, 62, 1)
    + '<rect class="f" x="44" y="82" width="632" height="76" rx="14"/>'
    + '<text class="m" x="62" y="110">Run: codex plugin marketplace add C:/Users/you/nextra-rhinogh-kit</text>'
    + '<text class="m" x="62" y="134">then: codex plugin add nextra-rhinogh@nextra</text>'
    + '<text class="s" x="44" y="190">The agent runs both commands. Then restart the app.</text>' + badge(400, 185, 2))
print("written to", out)
