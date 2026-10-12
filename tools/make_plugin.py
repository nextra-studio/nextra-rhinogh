"""plugin/nextra-rhinogh/: one plug-in folder that installs the bridge (MCP server) AND the skill in one step.
  - Claude Code:            .claude-plugin/plugin.json  (+ marketplace .claude-plugin/marketplace.json at the repo root)
  - Codex / ChatGPT desktop: .codex-plugin/plugin.json   (+ marketplace .agents/plugins/marketplace.json at the repo root)
Generated from skill/nextra and bridge/server; committed so the GitHub repo works as a marketplace."""
import json, os, shutil, sys

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
man = json.load(open(os.path.join(repo, "bridge", "manifest.json"), encoding="utf-8"))
ver, desc = man["version"], man["description"]
name = "nextra-rhinogh"
root = os.path.join(repo, "plugin", name)
url = "https://github.com/Tatsuki0123/nextra-rhinogh"
author = {"name": "Tatsuki", "url": url}


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

# ---- Claude Code
write(".claude-plugin/plugin.json", {
    "name": name, "displayName": "Nextra for Rhino + Grasshopper", "version": ver, "description": desc,
    "author": author, "homepage": url, "repository": url, "license": "Apache-2.0",
    "keywords": ["rhino", "grasshopper", "mcp", "karamba"],
    "mcpServers": {name: {"command": "node", "args": ["${CLAUDE_PLUGIN_ROOT}/server/index.js"], "env": {"NEXTRA_MODE": "full"}}},
})
write("/.claude-plugin/marketplace.json", {
    "name": "nextra", "owner": author, "description": "Nextra: Rhino 8 + Grasshopper tools and skill for AI agents",
    "plugins": [{"name": name, "source": f"./plugin/{name}", "description": desc, "version": ver}],
})

# ---- Codex / ChatGPT desktop (same shape as OpenAI's bundled plug-ins: cwd "." = this folder)
write("codex.mcp.json", {"mcpServers": {name: {"command": "node", "args": ["server/index.js"], "cwd": ".", "env": {"NEXTRA_MODE": "full"},
                                              "startup_timeout_sec": 30, "tool_timeout_sec": 300}}})
write(".codex-plugin/plugin.json", {
    "name": name, "version": ver, "description": desc, "author": author, "homepage": url, "repository": url,
    "license": "Apache-2.0", "keywords": ["rhino", "grasshopper", "mcp", "karamba"],
    "skills": "./skills/", "mcpServers": "./codex.mcp.json",
    "interface": {
        "displayName": "Nextra for Rhino", "shortDescription": "Build and edit Rhino 8 + Grasshopper models",
        "longDescription": desc + " Needs Rhino 8 running with the Nextra-RhinoGh plug-in (Rhino Package Manager).",
        "developerName": "Tatsuki", "category": "Design", "capabilities": ["Read", "Write"], "websiteURL": url,
        "defaultPrompt": ["Create a simple timber house in Rhino", "Show my Grasshopper definition"],
        "brandColor": "#1A7F37", "composerIcon": "./assets/icon.svg", "logo": "./assets/icon.svg",
    },
})
write("/.agents/plugins/marketplace.json", {
    "name": "nextra", "interface": {"displayName": "Nextra"},
    "plugins": [{"name": name, "source": {"source": "local", "path": f"./plugin/{name}"},
                 "policy": {"installation": "AVAILABLE", "authentication": "ON_INSTALL"}, "category": "Design"}],
})
print(f"{root}: plug-in {ver}")
sys.exit(0)
