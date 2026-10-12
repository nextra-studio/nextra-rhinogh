# Nextra for Rhino 8 + Grasshopper

Six token-lean tools let an LLM build and change Rhino models and Grasshopper definitions. It works from Claude Code, Claude Desktop or any MCP client. Replies say exactly what changed, images carry tags the model can point at, and every edit can be undone.

| tool | what it does |
|---|---|
| `view` | Rhino or GH canvas image with an 8×8 grid and short handle tags (`r12`, `g4`). Region-first: a crowded view returns per-cell counts, then `focus:'C3:E5'` zooms in. Also shows GH preview geometry in Rhino. |
| `query` | Text, cheapest first. **Rhino:** summary → one line per object with an oriented box → detail → vertices. **GH:** the definition as shorthand. Also searches installed components (with ports), the **API reference** (RhinoCommon, Grasshopper SDK, rhinoscriptsyntax), the docs, your workflows, the script library and status. |
| `run` | Python 3 / C# in Rhino with helpers (`h('r3')`, `q('layer:X')`, `obb()`, `ghdoc()`). Supports patch edits. Replies with the Rhino + GH delta, errors at your own line numbers, and a checkpoint. |
| `gh_edit` | Shorthand diffs: create, modify, wire, group, and component state (dropdowns, on-component toggles, right-click menu, clicks). Also script components, layout/tidy (with group interface relays), and open/save/bake. |
| `undo` | Exact revert to any checkpoint (Rhino objects + GH document), independent of Rhino's own undo stack. |
| `memory` | Your preferences and workflows, learned rules (user-approved), mistake log, script library. |

**Tokens.** Measured live in the same Rhino on three detailed scenarios: a 548-object office with edits, a GH attractor facade with edits, and a GH gridshell with edits. Input and output are counted separately, including schemas and docs; details in [test/v2/REPORT.md](test/v2/REPORT.md).

| one session | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| total tokens | **≈ 9.1k** | ≈ 32.9k (3.6×) | ≈ 64.9k (7.1×) |
| billed input, no cache | **≈ 122k** | ≈ 625k (5.1×) | ≈ 1.06M (8.7×) |

**Testing.** A regression suite ([test/regress](test/regress/regress.py)) runs every case in one Rhino session and checks the results: 194 steps.

## Install

1. **Rhino plug-in.** In Package Manager, install `dist/nextra-zero-0.1.0-rh8_35-win.yak` (drag it onto Rhino, or run `Yak.exe install <file>`), then restart Rhino.
   - **Status dot:** a small dot appears at the bottom-right of the Rhino window and of the GH canvas. Green = an LLM is connected, amber = ready, red = click to restart.
   - **Commands:** `Nextra`, `NextraOn`, `NextraOff`, `NextraFolder`.
2. **LLM side**
   - **Claude Desktop:** open `dist/nextra-rhinogh-0.1.0.mcpb`.
   - **Claude Code:** open this folder (`.mcp.json` and `.claude/skills/nextra-rhinogh` are project-scoped). Elsewhere, add the server with `claude mcp add nextra-rhinogh -- node <repo>/bridge/server/index.js`.
   - **claude.ai skills:** upload `dist/nextra-rhinogh-skill.zip`.
   - **Any other MCP client:** run `node bridge/server/index.js` over stdio (Node ≥ 18, no dependencies). LLMs other than Claude read the same docs through `query {scope:'docs'}`.
3. **Several Rhino windows.** The bridge binds to one (`NEXTRA_FILE=house.3dm`, or `query {instance:'house.3dm'}`) and never talks to another.

## Your own instructions

The plug-in creates a folder at first start, `%APPDATA%\Nextra\user`; the Rhino command `NextraFolder` opens it. It holds:

| file / folder | what the AI does with it |
|---|---|
| `preferences.md` | Read at the start of every session. |
| `workflows/<name>.md` | Each starts with a `when:` line; the AI opens the one matching your request. |
| `plugins/<Category>.md` | Rules attached automatically when that plug-in's components are used. |
| `scripts/` | Your scripts, available to `run`. |

The folder works however the skill is installed, including as a zip uploaded to claude.ai, because the plug-in serves it through the tools. Plug-in updates never touch it. The README in the folder explains how to write each file.

**Config** is in `%APPDATA%/Nextra/config.json`:

| key | meaning |
|---|---|
| `port` | server port |
| `enabled` | start the server with Rhino |
| `plugin_docs` | `auto`, `hint` or `off` |
| `library_dirs` | extra script folders |
| `user_dir` | location of your folder |
| `memory_dir` | location of the AI's memory |
| `auto_dismiss` | dialog titles to close automatically |
| `capture_copy_dir` | extra copy location for captured images |

## Repository

```
src/Nextra.Host     .rhp: 127.0.0.1 server (token), UI-thread dispatcher with timeouts + late results, dialog probe/dismiss, status dots, engine hot-reload
src/Nextra.Engine   all Rhino/GH logic (hot-reloaded on rebuild): tools, shorthand parser/builder/writer, layout, vision, checkpoints, memory, API reference, user folder
bridge/             MCP stdio bridge (zero-dependency Node) + .mcpb manifest
skill/nextra/       SKILL.md, shorthand.md, workflows.md, api/*, plugins/<Category>.md (auto-attached plug-in guides)
tools/              build.py, deploy_dev.ps1 (dev install with hot reload), package.ps1 (yak + mcpb + skill zip), nx.py (test client), mcp_http.py
test/               v1/ = first test round (cases 01-16 + first benchmark), v2/ = detailed benchmark with report and all raw results, regress/ = full regression suite
dev/                developer guide, original plan, build checklist, reference code (information only, not part of the skill)
```

## Develop

1. Run `powershell tools/deploy_dev.ps1` once, with Rhino closed.
2. Start Rhino.
3. Edit the engine code, then run `python tools/build.py`. The next call uses the new engine; no Rhino restart is needed. Host changes do need a restart.
4. Try calls with `python tools/nx.py query '{}'`, and check everything with `python test/regress/regress.py`.

See [dev/](dev/README.md) for the full developer guide.
