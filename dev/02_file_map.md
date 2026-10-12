# 02 — File map

Paths are relative to the repo root. Line counts are approximate.

## Root

| path | what |
|---|---|
| `README.md` | install, tool overview, config keys |
| `.mcp.json` | project-scoped MCP config for Claude Code: `node bridge/server/index.js` |
| `.claude/skills/nextra-rhinogh/` | **generated copy** of `skill/nextra/` so Claude Code finds the skill. Do not edit; `tools/deploy_dev.ps1` re-syncs it |
| `.gitignore` | bin, obj, `test/captures`, `test/calls.jsonl`, `test/_tmp`, `dist` |
| `dist/` | build output: `.yak` (Rhino) and `.mcpb` (Claude Desktop). Git-ignored |

## dev/ (information only, not part of the skill)

| path | what |
|---|---|
| `dev/README.md` + `01…06_*.md` | this guide |
| `dev/Nextra.zero_RhinoGh_Plan.md` | the original plan the build started from |
| `dev/BUILD_PLAN.md` | checklist of every requested item and its status |
| `dev/reference/nextra.gh/` | the old Nextra GH plug-in: architecture notes, its errors and solutions, hang/timeout logic, token optimisation notes, `mcp_server.py` |
| `dev/reference/rhino_tools/` | older Rhino tools: vision (grid, labels), oriented-bbox / structural utilities, with a write-up |
| `dev/reference/shiba_tidy/` | Shiba Tidy C# script, the idea behind the `relays` option |

## skill/nextra/ — what the LLM reads

| file | content |
|---|---|
| `SKILL.md` | entry point: priorities, handles, regions, geometry depth, recovery. Short on purpose |
| `shorthand.md` | the GH shorthand grammar with examples (read once per session) |
| `workflows.md` | recipes: Rhino build/modify, GH build/modify/tidy, Rhino↔GH, scripts, structural |
| `api/rhino-python.md`, `rhino-csharp.md` | cheat sheets for `run` code in Rhino |
| `api/gh-python.md`, `gh-csharp.md` | cheat sheets for script components and for driving GH from `run`, plus custom component UI |
| `plugins/<Category>.md` | one guide per GH plug-in. The `## Rules` section is auto-attached once per session when that plug-in's components are used. `_TEMPLATE.md` shows the format |

These files are also served to any non-Claude LLM through `query {scope:'docs'}`.

## bridge/

| file | content |
|---|---|
| `server/index.js` (~190 lines) | MCP over stdio: tool schemas (`TOOLS`), instance discovery and binding, `post()` to Rhino, heartbeat, image → base64 |
| `manifest.json` | `.mcpb` manifest for Claude Desktop |

## src/Nextra.Host/ — the .rhp (change needs Rhino restart)

| file | lines | what |
|---|---|---|
| `HostPlugIn.cs` | 100 | plug-in class, headless guard, `TurnOn`/`TurnOff`, commands `Nextra`, `NextraOn`, `NextraOff` |
| `Server.cs` | 250 | `TcpListener`, tiny HTTP parser (`HttpLite`), token check, routes, `WriteInfo` (server-pid.json), `Config` get/set |
| `Dispatcher.cs` | 205 | UI-thread execution, queue, timeouts, late results, status/dismiss |
| `DialogProbe.cs` | 190 | finds, reads (Win32 + UI Automation for WPF/Eto) and closes blocking dialogs, never touching the UI thread |
| `EngineLoader.cs` | 115 | loads `Nextra.Engine.dll` from bytes into a collectible load context, owns the `State` bag |
| `StatusDot.cs` | 150 | the Rhino status light form |
| `GhDot.cs` | 105 | the GH canvas status light |

## src/Nextra.Engine/ — the logic (hot-reloaded)

### Core/ — shared by every tool
| file | lines | what |
|---|---|---|
| `Entry.cs` | 90 | `Call(tool, argsJson, state)` dispatch, error handling, `NxError`, `Log` |
| `Args.cs` | 150 | tolerant argument reader, `Reply` envelope |
| `Handles.cs` | 130 | `Handles` (r/g tables), `Fmt` (compact numbers/vectors, `Trunc`, `FilterTokens`, `Like`) |
| `ViewTool.cs` | 570 | the `view` tool: Rhino capture, cell zoom, occlusion, tag placement, `EngineConfig` |
| `Labels.cs` | 190 | `LabelPlacer` (rotated text, halo, overlap test) and `GridOverlay` (8×8) |
| `QueryTool.cs` | 95 | the `query` tool: routes to Rhino / GH / components / docs / library / status |
| `RunTool.cs` | 370 | the `run` tool: RhinoCode execution, helpers, patches, error context |
| `Checkpoints.cs` | 115 | checkpoint list + the `undo` tool |
| `Memory.cs` | 350 | `Memory` (prefs/rules/mistakes), `MemoryTool`, `Library`, `DocsStore` |
| `ApiDocs.cs` | 560 | API reference: builds a cleaned index of RhinoCommon + Grasshopper SDK (reflection + shipped XML docs) + rhinoscriptsyntax (Python sources), cached per Rhino version; ranked search for `query {scope:'api'}` |
| `UserDocs.cs` | 230 | the user's folder (`%APPDATA%/Nextra/user`): preferences, workflows, plug-in guides, scripts; seeded with README + templates |

### Rh/ — Rhino side
| file | lines | what |
|---|---|---|
| `Geo.cs` | 400 | oriented box + shape class, one-line descriptions, detail, verts |
| `Selector.cs` | 130 | the filter language (`layer:X type:curve bbox:…`) → objects |
| `DeltaTracker.cs` | 175 | records adds/modifies/deletes during a call; exact revert; compact report |

### Gh/ — Grasshopper side
| file | lines | what |
|---|---|---|
| `GhCore.cs` | 205 | GH document access without UI blocking, open/switch, archive, GH delta |
| `GhEditTool.cs` | 270 | the `gh_edit` tool: ops, script edits, open/new/save, bake, layout, report |
| `GhQuery.cs` | 205 | `query scope:'gh'`: summary / list / detail / data |
| `GhView.cs` | 275 | GH canvas image (+ `GhPreviewView` for unbaked geometry in Rhino) |
| `ComponentSearch.cs` | 275 | component lookup from `ComponentServer` + port resolution on live objects |
| `Shorthand/Parser.cs` | 465 | text → ops (AST) |
| `Shorthand/Builder.cs` | 590 | ops → canvas changes, per-line errors |
| `Shorthand/Writer.cs` | 335 | canvas → text (reverse engine) |
| `Layout.cs` | 630 | Sugiyama micro/macro layout, place/tidy, relays, bounds repair |
| `StateIO.cs` | 195 | generic `$key` component state through GH archives |
| `MenuIO.cs` | 100 | right-click menu items and on-component clicks |
| `Scripts.cs` | 245 | Python / C# script components: ports, code, mirror files |
| `PluginDocs.cs` | 90 | plug-in guide loader and once-per-session attach |

Project files: `Nextra.Engine.csproj`, `Nextra.Host.csproj`, and `src/Directory.Build.props`, which references RhinoCommon and Eto and sets `IncludeSourceRevisionInInformationalVersion=false`.

## tools/

| file | use |
|---|---|
| `build.py` | `python tools/build.py [Nextra.Engine\|Nextra.Host]` builds and prints de-duplicated errors |
| `deploy_dev.ps1` | **Rhino closed.** Builds both projects, installs the dev package folder, points `config.json` at the build output so hot reload works, syncs `.claude/skills` |
| `package.ps1` | release: Yak package + `.mcpb` + claude.ai skill zip into `dist/` |
| `make_skill_zip.py` | `dist/nextra-rhinogh-skill.zip` (folder `nextra-rhinogh/` with SKILL.md, forward-slash entries) |
| `nx.py` | test client: `python tools/nx.py <tool> '<json>'`, `--code file.py`, `health`, `reload`; logs to `test/calls.jsonl` with token estimates |
| `mcp_http.py` | helper for the benchmark: talks MCP to other servers over HTTP/stdio |

## test/

`test/v1/` — the first test round: one folder per requested case (`01_rhino_from_scratch` … `16_messy_tidy`), plus `extra_component_controls`, `status_dot`, `memory_test`, and `11_compare` (first benchmark). `test/v2/` — the detailed benchmark. `test/regress/` — the regression suite.
Each holds the images the LLM received and the produced `.3dm` / `.gh` / scripts.
- `test/regress/regress.py` — **the regression suite**: every case in one Rhino session, in order, with checks (194 steps). Scripts it runs are in `test/regress/scripts/`. Outputs go to `test/regress/out/` (git-ignored); each run uses a fresh memory folder.
- `v1/11_compare/` — first benchmark (v1 report + harness `bench_*.py`); `test/v2/` is the detailed one (`bench_v2.py` live harness, `ghspec.py` neutral GH specs + per-system emitters, `office.py`, `gen_report.py`, raw calls in `v2/out/`).
- `test/README.md` — the results table.

## Which file do I open for…

| change | open |
|---|---|
| add or rename a tool argument | `bridge/server/index.js` (schema) and the tool's `Run` (`Core/*Tool.cs`, `Gh/GhEditTool.cs`) |
| new shorthand syntax | `Parser.cs` (parse) → `Builder.cs` (apply) → `Writer.cs` (print) → `skill/nextra/shorthand.md` |
| a component setting that does not take | `Builder.ApplyConfig`, `StateIO.cs`, `MenuIO.cs` |
| ambiguous or wrong component picked | `ComponentSearch.Resolve` |
| layout looks wrong | `Layout.cs` (`Layer`, `Move`, `SetPivot`, `RepairBounds`) |
| reply text too long or unclear | the tool's `Run`, or `Fmt`/`Geo.Describe` |
| image tags overlap or are missing | `Labels.cs`, `ViewTool.RhinoLabel`, `GhView.cs` |
| dialog hangs Rhino | `Dispatcher.cs` (`DialogProbe`) |
| a plug-in needs special rules | add `skill/nextra/plugins/<Category>.md` |
| dots | `StatusDot.cs`, `GhDot.cs` |
