# Nextra for Rhino 8 + Grasshopper

Six token-lean tools that let an LLM (Claude Code, Claude Desktop, any MCP client) model in Rhino and build, edit and tidy Grasshopper definitions — with images it can point at, edits it can undo, and replies that say exactly what happened.

| tool | what it does |
|---|---|
| `view` | Rhino or GH canvas image with an 8×8 grid and short handle tags (`r12`, `g4`); region-first (crowded → per-cell counts → `focus:'C3:E5'`); GH preview geometry in Rhino |
| `query` | text, cheapest first: Rhino summary → one-line objects with oriented boxes → detail → vertices; GH as shorthand; installed components with ports; docs; script library; status |
| `run` | Python 3 / C# in Rhino with helpers (`h('r3')`, `q('layer:X')`, `obb()`, `ghdoc()`), patch edits, Rhino + GH delta, error lines, checkpoint |
| `gh_edit` | shorthand diffs (create, modify, wire, group, component state incl. dropdowns, on-component toggles, right-click menu, clicks), script components, layout/tidy (+ group interface relays), open/save/bake |
| `undo` | exact revert to any checkpoint (Rhino objects + GH document) |
| `memory` | prefs, learned rules (user-approved), mistake log, script library |

Measured on the same tasks in the same Rhino session (see `test/11_compare/REPORT.md`): **≈ 3.4k tokens** for a Rhino + GH scenario vs **≈ 24k** (McNeel Rhino-MCP-Platform) and **≈ 52k** (RhinoMCP); schema 1.3k vs 4.8k / 22.9k.

## Install

1. **Rhino plug-in**: Package Manager → install `dist/nextra-zero-0.1.0-rh8_35-win.yak` (drag onto Rhino, or `Yak.exe install <file>`). Restart Rhino. A small dot appears at the bottom-right of the Rhino window and the GH canvas: green = LLM connected, amber = ready, red = click to restart. Commands: `Nextra`, `NextraOn`, `NextraOff`.
2. **LLM side**
   - Claude Desktop: open `dist/nextra-rhinogh-0.1.0.mcpb`.
   - Claude Code: open this folder (`.mcp.json` + `.claude/skills/nextra-rhinogh` are project-scoped), or add the server elsewhere: `claude mcp add nextra-rhinogh -- node <repo>/bridge/server/index.js`.
   - Any other MCP client: stdio command `node bridge/server/index.js` (Node ≥ 18, no dependencies). Docs for non-Claude LLMs: `query {scope:'docs'}`.
3. Several Rhino windows: the bridge binds to one (`NEXTRA_FILE=house.3dm` or `query {instance:'house.3dm'}`) and never talks to another.

Config `%APPDATA%/Nextra/config.json`: `port`, `enabled`, `plugin_docs` (`auto|hint|off`), `library_dirs` (your script folders), `memory_dir`, `auto_dismiss` (dialog titles to close automatically), `capture_copy_dir`.

## Repository

```
src/Nextra.Host     .rhp: 127.0.0.1 server (token), UI-thread dispatcher with timeouts + late results, dialog probe/dismiss, status dots, engine hot-reload
src/Nextra.Engine   all Rhino/GH logic (hot-reloaded on rebuild): tools, shorthand parser/builder/writer, layout, vision, checkpoints, memory
bridge/             MCP stdio bridge (zero-dependency Node) + .mcpb manifest
skill/nextra/       SKILL.md, shorthand.md, workflows.md, api/*, plugins/<Category>.md (auto-attached plug-in guides)
tools/              build.py, deploy_dev.ps1 (dev install with hot reload), package.ps1 (yak + mcpb), nx.py (test client), mcp_http.py
test/               evidence per case (images, .gh/.3dm, reports); 11_compare = benchmark harness + REPORT.md
reference/          code the design learned from (nextra.gh, rhino_tools vision/OBB, Shiba Tidy)
BUILD_PLAN.md       checklist of everything requested and its status
```

## Develop

`powershell tools/deploy_dev.ps1` once (Rhino closed) → start Rhino → edit engine code → `python tools/build.py` → the next call uses the new engine (no Rhino restart). Host changes need a Rhino restart. Try calls with `python tools/nx.py query '{}'`.
