# Nextra for Rhino 8 + Grasshopper

Six token-lean tools let an LLM build and change Rhino models and Grasshopper definitions. It works from Claude Code, Claude Desktop or any MCP client. Replies say exactly what changed, images carry tags the model can point at, and every edit can be undone.

| tool | what it does |
|---|---|
| `view` | Rhino or GH canvas image with an 8×8 grid and short handle tags (`r12`, `g4`). Region-first: a crowded view returns per-cell counts, then `focus:'C3:E5'` zooms in. Also shows GH preview geometry in Rhino. |
| `query` | Text, cheapest first. **Rhino:** summary → one line per object with an oriented box → detail → vertices. **GH:** the definition as shorthand. Also searches installed components (with ports), the **API reference** (RhinoCommon, Grasshopper SDK, rhinoscriptsyntax), the docs, your workflows, the script library and status. |
| `run` | Python 3 / C# in Rhino with helpers (`h('r3')`, `q('layer:X')`, `obb()`, `ghdoc()`). Supports patch edits. Replies with the Rhino + GH delta, errors at your own line numbers, and a checkpoint. |
| `gh_edit` | Shorthand diffs: create, modify, wire, group, and component state (dropdowns, on-component toggles, right-click menu, clicks). Also script components, layout/tidy (with group interface relays), and open/save/bake. Definitions can be saved as **`.ghs`**: the same shorthand as readable, diffable text that opens back into an identical canvas. |
| `undo` | Exact revert to any checkpoint (Rhino objects + GH document), independent of Rhino's own undo stack. |
| `memory` | Your preferences and workflows, learned rules (user-approved), mistake log, script library. |

**Tokens.** All three MCPs were measured live in the same Rhino. Input and output are counted separately, including schemas and docs. Each result was checked neutrally, and every case gave the same correct model on all three.

**v3** ([test/v3/REPORT.md](test/v3/REPORT.md)) covers 19 cases in one session. They include:
- Rhino frames
- GH towers and fins
- Python and C# components
- Karamba beam, house FE and shell
- data trees, a messy canvas, a freeform canopy, and a 1,224-object model

| one session | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP |
|---|---|---|---|
| tool calls (Python fallbacks) | **43 (0)** | 108 (34) | 96 (32) |
| total tokens | **≈ 16.2k** | ≈ 77.0k (4.8×) | ≈ 237.7k (14.7×) |
| billed input, no cache | **≈ 0.43M** | ≈ 4.12M (9.7×) | ≈ 11.46M (27×) |

The earlier v2 benchmark (office, facade and gridshell) is in [test/v2/REPORT.md](test/v2/REPORT.md): 3.6× and 7.1× fewer tokens than the other two.

**Testing.** A regression suite ([test/regress](test/regress/regress.py)) runs every case in one Rhino session and checks the results: 238 steps, all passing.

## Install

**Requirements:** Windows, Rhino 8 (a recent service release), and Claude Desktop (or another MCP client, see [other clients](#other-clients)).

Nextra has **three parts**, and all three are needed:

![The three parts: Rhino plug-in, bridge, skill](docs/img/overview.svg)

| # | Part | Where to get it |
|---|---|---|
| 1 | **Rhino plug-in** | Rhino Package Manager, search `Nextra-RhinoGh` |
| 2 | **Bridge** (`nextra-rhinogh-<version>.mcpb`) | [Latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest) |
| 3 | **Skill** (`nextra-rhinogh-skill.zip`) | [Latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest) |

### Step 1: Rhino plug-in

1. In Rhino, type **`PackageManager`** in the command line and press Enter.
2. Search for **`Nextra-RhinoGh`** and click **Install**.
3. **Close and restart Rhino.** The plug-in only loads at startup.
4. Look at the bottom-right corner of the Rhino window. A small dot means it works:

   ![The status dot in the bottom-right corner of Rhino](docs/img/status-dot-real.png)

   ![Dot colours: green = connected, amber = ready, red = restart](docs/img/dot-states.svg)

   If there is no dot, type `NextraOn`. If Rhino says `Unknown command: NextraOn`, see [Troubleshooting](#troubleshooting).

> **No Package Manager?** Download `nextra-rhinogh-<version>-rh8_35-win.yak` from the [latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest), drag it onto the Rhino window, and restart Rhino.

Other Rhino commands: `Nextra`, `NextraOff`, `NextraFolder` (opens your own instructions folder, see [below](#your-own-instructions)).

### Step 2: Bridge (gives Claude the tools)

1. Download `nextra-rhinogh-<version>.mcpb` from the [latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest).
2. **Double-click the file.** Claude Desktop opens an install dialog; confirm it. (Or open Claude Desktop → **Settings → Extensions → Advanced settings → Install Extension…** and pick the file.)
3. **Fully quit Claude Desktop**, including its icon in the system tray, and start it again.
4. In a new chat, **nextra-rhinogh** should appear in the list of tools/connectors.

### Step 3: Skill (teaches Claude how to use the tools)

1. Download `nextra-rhinogh-skill.zip` from the [latest release](https://github.com/Tatsuki0123/nextra-rhinogh/releases/latest). **Do not unzip it.**
2. In Claude, open **Settings → Capabilities → Skills** and upload the zip. (Menu names can differ slightly between Claude versions.)

### Step 4: Try it

Keep Rhino open with the dot showing and ask Claude:

> *Create a simple timber house in Rhino.*

Claude should call the `nextra-rhinogh` tools, and the dot turns green while it works.

### Troubleshooting

| What you see | What to do |
|---|---|
| Claude says **"Nextra isn't connected"** or can't find any Rhino tools | The bridge (step 2) is missing. The skill alone has no tools. Install the `.mcpb` and fully restart Claude Desktop. |
| `Unknown command: NextraOn` in Rhino | The plug-in isn't loaded. Restart Rhino after installing. Still missing? Run `PlugInManager`, find **Nextra.Host**, right-click → **Load**. |
| `Blocking plug-in Nextra.Host` | Usually after uninstalling and reinstalling in one session. Close **all** Rhino windows and start Rhino again. |
| No dot in Rhino | Type `NextraOn`. |
| Dot is **red** | Click it to restart the server. |
| Claude says **"Rhino is not reachable"** | Rhino isn't running, or the plug-in is off (`NextraOn`). Don't let Claude retry in a loop. |
| Several Rhino windows open | The bridge binds to one file. Tell Claude which (for example *"use house.3dm"*), or set `NEXTRA_FILE=house.3dm`. It never talks to another window. |

### Other clients

- **Claude Code:** open this repo folder (`.mcp.json` and `.claude/skills/nextra-rhinogh` are project-scoped). Elsewhere: `claude mcp add nextra-rhinogh -- node <repo>/bridge/server/index.js`.
- **Any other MCP client:** run `node bridge/server/index.js` over stdio (Node 18 or later, no dependencies). LLMs other than Claude read the same docs through `query {scope:'docs'}`.

<details>
<summary>Maintainers: making a release</summary>

Bump the version in `tools/package.ps1`, `tools/deploy_dev.ps1`, `bridge/manifest.json`, `bridge/server/index.js` and both `.csproj` files, then:

```
tools/package.ps1
yak push dist/nextra-rhinogh-<ver>-rh8_35-win.yak
gh release create v<ver> dist/nextra-rhinogh-<ver>.mcpb dist/nextra-rhinogh-skill.zip dist/nextra-rhinogh-<ver>-rh8_35-win.yak
```

</details>

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
skill/nextra/       SKILL.md, shorthand.md, workflows.md, api/*
guides/plugins/     plug-in guides (<Category>.md, auto-attached); shipped inside the Rhino plug-in package
tools/              build.py, deploy_dev.ps1 (dev install with hot reload), package.ps1 (yak + mcpb + skill zip), nx.py (test client), mcp_http.py
test/               v1/ = first round (cases 01-16), v2/ = detailed benchmark, v3/ = all cases on three systems, regress/ = full regression suite
dev/                developer guide, original plan, build checklist, reference code (information only, not part of the skill)
```

## Develop

1. Run `powershell tools/deploy_dev.ps1` once, with Rhino closed.
2. Start Rhino.
3. Edit the engine code, then run `python tools/build.py`. The next call uses the new engine; no Rhino restart is needed. Host changes do need a restart.
4. Try calls with `python tools/nx.py query '{}'`, and check everything with `python test/regress/regress.py`.

See [dev/](dev/README.md) for the full developer guide.

## License

[Apache License 2.0](LICENSE). Free to use, including commercially. Keep the copyright and license notice ([NOTICE](NOTICE)) and mark any changes you make. The license also grants a patent right to use the code.
