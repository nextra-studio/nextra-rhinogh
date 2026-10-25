"""plugin/nextra-rhinogh/: one plug-in folder that installs the bridge (MCP server) AND the skill in one step.
  - Claude Code / Desktop:   .claude-plugin/plugin.json + .mcp.json (+ catalog .claude-plugin/marketplace.json at the repo root)
  - Standalone ZIP:          the same folder, zipped
  - Codex / ChatGPT desktop: .codex-plugin/plugin.json   (+ marketplace .agents/plugins/marketplace.json at the repo root)
Generated from skill/nextra and bridge/server; committed so the GitHub repo works as a marketplace."""
import json, os, shutil, sys

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
man = json.load(open(os.path.join(repo, "bridge", "manifest.json"), encoding="utf-8"))
ver, desc = man["version"], man["description"]
name = "nextra-rhinogh"
root = os.path.join(repo, "plugin", name)
url = "https://github.com/nextra-studio/nextra-rhinogh"   # repository
site = "https://nextra.studio/"                            # homepage
author = {"name": "Tatsuki", "url": site}


def write(rel, obj):
    p = os.path.join(root if not rel.startswith("/") else repo, rel.lstrip("/"))
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f:
        if isinstance(obj, str): f.write(obj)
        else: json.dump(obj, f, indent=2); f.write("\n")


shutil.rmtree(root, ignore_errors=True)   # OneDrive can keep an empty folder alive: copy over it
shutil.copytree(os.path.join(repo, "skill", "nextra"), os.path.join(root, "skills", name), dirs_exist_ok=True)
shutil.copytree(os.path.join(repo, "bridge", "server"), os.path.join(root, "server"), dirs_exist_ok=True)
os.makedirs(os.path.join(root, "assets"), exist_ok=True)
shutil.copy(os.path.join(repo, "docs", "img", "icon.svg"), os.path.join(root, "assets", "icon.svg"))

# ---- Claude Code / Claude Desktop (same shape as anthropics/knowledge-work-plugins: the plug-in folder carries its own
# .claude-plugin/plugin.json + .mcp.json; the desktop app's server-side marketplace sync rejects manifest-free folders)
claude_manifest = {
    "name": name, "version": ver, "description": desc,
    "author": author, "homepage": site, "repository": url, "license": "Apache-2.0",
    "keywords": ["rhino", "grasshopper", "mcp", "karamba"],
}
write(".claude-plugin/plugin.json", claude_manifest)
write(".mcp.json", {"mcpServers": {name: {"command": "node", "args": ["${CLAUDE_PLUGIN_ROOT}/server/index.js"], "env": {"NEXTRA_MODE": "full"}}}})
write("/.claude-plugin/marketplace.json", {
    "name": "nextra-rhinogh", "owner": {"name": "Tatsuki"}, "description": "NextraRhinoGh: Rhino 8 + Grasshopper tools and skill for AI agents",
    "plugins": [{"name": name, "displayName": "NextraRhinoGh", "description": desc, "version": ver, "category": "design",
                 "source": f"./plugin/{name}", "homepage": site}],
})

# ---- Codex / ChatGPT desktop (same shape as OpenAI's bundled plug-ins: cwd "." = this folder)
write("codex.mcp.json", {"mcpServers": {name: {"command": "node", "args": ["server/index.js"], "cwd": ".", "env": {"NEXTRA_MODE": "full"},
                                              "startup_timeout_sec": 30, "tool_timeout_sec": 300}}})
write(".codex-plugin/plugin.json", {
    "name": name, "version": ver, "description": desc, "author": author, "homepage": site, "repository": url,
    "license": "Apache-2.0", "keywords": ["rhino", "grasshopper", "mcp", "karamba"],
    "skills": "./skills/", "mcpServers": "./codex.mcp.json",
    "interface": {
        "displayName": "NextraRhinoGh", "shortDescription": "Rhino + Grasshopper tools",
        "longDescription": desc + " Needs Rhino 8 running with the Nextra-RhinoGh plug-in (Rhino Package Manager).",
        "developerName": "Tatsuki", "category": "Design", "capabilities": ["Read", "Write"], "websiteURL": site,
        "defaultPrompt": ["Create a timber skyscraper in Rhino", "Create a parametric skyscraper in Grasshopper"],
        "brandColor": "#1A7F37", "composerIcon": "./assets/icon.svg", "logo": "./assets/icon.svg",
    },
})
write("/.agents/plugins/marketplace.json", {
    "name": "nextra-rhinogh", "interface": {"displayName": "NextraRhinoGh"},
    "plugins": [{"name": name, "source": {"source": "local", "path": f"./plugin/{name}"},
                 "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"}, "category": "Design"}],
})
# Ready-made plugin ZIPs for desktop import, with the same tools and skill.
import zipfile
zp = os.path.join(repo, "dist", f"{name}-plugin.zip")
os.makedirs(os.path.dirname(zp), exist_ok=True)
with zipfile.ZipFile(zp, "w", zipfile.ZIP_DEFLATED) as z:
    for d, _, files in os.walk(root):
        for f in sorted(files):
            full = os.path.join(d, f)
            z.write(full, os.path.relpath(full, root).replace(os.sep, "/"))
print(f"{root}: plug-in {ver}  ->  {zp}")
gpt_zp = os.path.join(repo, "dist", f"{name}-gpt-plugin.zip")
shutil.copyfile(zp, gpt_zp)
print(f"OpenAI plug-in {ver}  ->  {gpt_zp}")

# ---- dist/nextra-rhinogh-agent-kit.zip: ONE folder with skill + MCP server for ChatGPT/Codex desktop (as a local
# marketplace: `codex plugin marketplace add <folder>`), Claude Code and any other agent that is pointed at the folder.
KIT = f"{name}-kit"
kit = os.path.join(repo, "dist", KIT)
shutil.rmtree(kit, ignore_errors=True)
skill_src = os.path.join(repo, "skill", "nextra")
for rel in (f".agents/skills/{name}", f".claude/skills/{name}", f"skills/{name}"):
    shutil.copytree(skill_src, os.path.join(kit, rel), dirs_exist_ok=True)
shutil.copytree(os.path.join(repo, "bridge", "server"), os.path.join(kit, "server"), dirs_exist_ok=True)
shutil.copytree(root, os.path.join(kit, "plugin", name), dirs_exist_ok=True)
for rel in (".claude-plugin/marketplace.json", ".agents/plugins/marketplace.json"):
    os.makedirs(os.path.dirname(os.path.join(kit, rel)), exist_ok=True)
    shutil.copy(os.path.join(repo, rel), os.path.join(kit, rel))


def kwrite(rel, text):
    p = os.path.join(kit, rel)
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, "w", encoding="utf-8", newline="\n") as f: f.write(text)


kwrite(".mcp.json", json.dumps({"mcpServers": {name: {"command": "node", "args": ["server/index.js"], "env": {"NEXTRA_MODE": "full"}}}}, indent=2) + "\n")
kwrite(".codex/config.toml", f'[mcp_servers.{name}]\ncommand = "node"\nargs = ["server/index.js"]\n\n[mcp_servers.{name}.env]\nNEXTRA_MODE = "full"\n')
AGENTS = f"""# NextraRhinoGh for Rhino 8 + Grasshopper (agent kit {ver})

This folder lets an AI agent drive Rhino 8 and Grasshopper. Everything is already here.

1. **Needs Rhino.** Rhino 8 must be running with the Nextra-RhinoGh plug-in (Rhino Package Manager). A dot in the
   bottom-right corner of the Rhino window means it works. Node 18+ must be installed.
2. **Tools (MCP server):** `node server/index.js` over stdio, run from this folder. It is already registered for
   Claude Code (`.mcp.json`) and Codex (`.codex/config.toml`) when this folder is the workspace. For any other client,
   register command `node` with argument `<this folder>/server/index.js`.
3. **Skill:** read `skills/{name}/SKILL.md` before using the tools (also at `.agents/skills/` and `.claude/skills/`).
4. Six tools: view, query, run, gh_edit, undo, memory. Start with `memory {{action:'list'}}`, then `query` / `view`.

ChatGPT / Codex desktop: open this folder as a Codex project, trust it when prompted, restart the desktop
app, then open Plugins Directory > NextraRhinoGh > NextraRhinoGh > Install. Keep this folder on your computer.
Claude Code: `/plugin marketplace add <this folder>` then `/plugin install {name}@nextra-rhinogh`.
"""
kwrite("AGENTS.md", AGENTS)
kwrite("CLAUDE.md", "@AGENTS.md\n")
kz = os.path.join(repo, "dist", f"{name}-agent-kit.zip")
with zipfile.ZipFile(kz, "w", zipfile.ZIP_DEFLATED) as z:
    for d, _, files in os.walk(kit):
        for f in sorted(files):
            full = os.path.join(d, f)
            z.write(full, KIT + "/" + os.path.relpath(full, kit).replace(os.sep, "/"))
shutil.rmtree(kit, ignore_errors=True)
print(f"agent kit {ver}  ->  {kz}")
sys.exit(0)
