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

# 2 Claude Desktop: double-click .mcpb → install dialog
svg("claude-desktop.svg", 720, 260,
    '<rect class="win" x="24" y="40" width="190" height="180" rx="10"/>'
    + '<path d="M94 72h36l18 18v58h-54z" fill="#bf8700"/><path d="M130 72v18h18" fill="#e3b341"/>'
    + '<text class="bt" x="121" y="128" text-anchor="middle">.mcpb</text>'
    + '<text class="m" x="119" y="176" text-anchor="middle">nextra-rhinogh</text><text class="m" x="119" y="194" text-anchor="middle">-0.1.2.mcpb</text>'
    + badge(200, 58, 1) + '<text class="s" x="119" y="240" text-anchor="middle">double-click the file</text>'
    + '<path d="M232 130h48" stroke="#57606a" stroke-width="2"/><path d="M280 124l10 6-10 6z" fill="#57606a"/>'
    + window(304, 24, 392, 212, "Claude")
    + '<text class="t" x="328" y="88">Install extension?</text>'
    + '<text class="s" x="328" y="114">Nextra for Rhino + Grasshopper</text><text class="s" x="328" y="134">view · query · run · gh_edit · undo · memory</text>'
    + button(560, 170, 110, "Install", n=2)
    + '<text class="s" x="328" y="192">then quit Claude fully</text><text class="s" x="328" y="210">(tray icon) and reopen</text>')

# 3 Claude: upload the skill
svg("claude-skill.svg", 720, 250,
    window(20, 16, 680, 218, "Claude — Settings")
    + '<rect class="nav" x="21" y="51" width="170" height="182"/>'
    + '<text class="s" x="40" y="80">General</text><rect class="navsel" x="30" y="94" width="152" height="28" rx="6"/>'
    + '<text class="t" x="40" y="113">Capabilities</text><text class="s" x="40" y="146">Extensions</text><text class="s" x="40" y="178">Connectors</text>'
    + badge(176, 108, 1)
    + '<text class="t" x="216" y="84">Skills</text><text class="s" x="216" y="106">Upload a .zip that contains SKILL.md — do not unzip it</text>'
    + '<rect class="f" x="216" y="126" width="300" height="40" rx="8"/><text class="m" x="230" y="151">nextra-rhinogh-skill.zip</text>'
    + button(536, 130, 130, "Upload skill", n=2))

# 4 Claude Code / terminal
svg("claude-code.svg", 720, 170,
    '<rect class="term" x="20" y="16" width="680" height="138" rx="10"/>'
    + '<circle cx="38" cy="33" r="5" fill="#ff5f57"/><circle cx="54" cy="33" r="5" fill="#febc2e"/><circle cx="70" cy="33" r="5" fill="#28c840"/>'
    + '<text class="tp" x="40" y="68">&gt;</text><text class="tt" x="56" y="68">/plugin install nextra-rhinogh --marketplace Tatsuki0123/nextra-rhinogh</text>'
    + '<text class="tg" x="40" y="98">✔ Installed nextra-rhinogh (MCP server + skill)</text>'
    + '<text class="tp" x="40" y="126">  restart Claude Code, then ask: "Create a simple timber house in Rhino"</text>')

# 5 ChatGPT / Codex desktop: one prompt
svg("chatgpt-prompt.svg", 720, 210,
    window(20, 16, 680, 178, "ChatGPT / Codex desktop — Codex or Work mode")
    + '<rect class="f" x="44" y="66" width="632" height="76" rx="14"/>'
    + '<text class="m" x="62" y="94">Run: codex plugin marketplace add Tatsuki0123/nextra-rhinogh</text>'
    + '<text class="m" x="62" y="118">then: codex plugin add nextra-rhinogh@nextra</text>'
    + badge(676, 66, 1)
    + '<text class="s" x="44" y="170">The agent runs both commands. Then restart the app.</text>' + badge(400, 165, 2))

# 6 ChatGPT / Codex desktop: add the server by hand
svg("chatgpt-settings.svg", 720, 330,
    window(20, 16, 680, 298, "ChatGPT / Codex desktop — Settings › MCP servers › Add server")
    + field(44, 72, 300, "Name", "nextra-rhinogh") + badge(352, 98, 1)
    + '<text class="s" x="380" y="72">Type</text><circle cx="392" cy="98" r="7" fill="#1f883d"/><text class="t" x="406" y="103">STDIO</text>'
    + '<circle cx="482" cy="98" r="7" fill="none" stroke="#57606a"/><text class="s" x="496" y="103">Streamable HTTP</text>' + badge(640, 98, 2)
    + field(44, 140, 120, "Command", "node") + badge(172, 166, 3)
    + field(44, 202, 560, "Argument", "C:\\Users\\&lt;you&gt;\\AppData\\Roaming\\Nextra\\bridge\\index.js") + badge(612, 228, 4)
    + '<text class="s" x="44" y="282">Tip: in Rhino run  Nextra › CopyBridgePath  and paste.</text>'
    + button(560, 262, 110, "Save", n=5))
print("written to", out)
