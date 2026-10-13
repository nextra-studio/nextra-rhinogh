# dev/ — developer guide to Nextra RhinoGh

Read this folder to understand how the system is built and how to change it.
The user-facing docs are elsewhere: `README.md` covers install and the tool overview, and `skill/nextra/` holds what the LLM reads.

| file | read it when you want to know… |
|---|---|
| [01_architecture.md](01_architecture.md) | how a request travels from the LLM to Rhino and back, the threads, hot reload, instance binding, status dots |
| [02_file_map.md](02_file_map.md) | what every file and folder in the repo is for, and which file to open for a given change |
| [03_tools.md](03_tools.md) | the 6 tools: every argument, what each one does inside, real example replies |
| [04_shorthand.md](04_shorthand.md) | the GH shorthand in depth: grammar, how Parser → Builder → Writer work, worked examples, `.ghs` files |
| [05_internals.md](05_internals.md) | the subsystems behind the tools: handles, deltas, checkpoints, oriented boxes, vision, layout, component state, scripts, memory, plug-in guides |
| [06_workflow.md](06_workflow.md) | building, hot reload, testing with `nx.py`, packaging, adding features, debugging, known pitfalls |
| [07_plugins_and_marketplaces.md](07_plugins_and_marketplaces.md) | Claude and OpenAI folder rules, marketplace discovery, ZIP layout, workspace settings, and implementation audit |
| [Nextra.zero_RhinoGh_Plan.md](Nextra.zero_RhinoGh_Plan.md) | the original plan the build started from |
| [BUILD_PLAN.md](BUILD_PLAN.md) | checklist of every requested item and its status |
| [reference/](reference/) | earlier code the design learned from: old nextra.gh, rhino_tools vision/OBB, Shiba Tidy |
| [reports/2026-10-13-tool-audit.md](reports/2026-10-13-tool-audit.md) | reproduced tool bugs, repairs, before/after images, verification, and remaining limits |

This folder is information only. It is not part of the skill: the skill is `skill/nextra/`, synced to `.claude/skills/`, and served by `query {scope:'docs'}`. Nothing here is loaded by the LLM or the plug-in.

## The system in one picture

```
 LLM client (Claude Code / Desktop / any MCP)
      │  stdio JSON-RPC (MCP): 6 tools
      ▼
 bridge/server/index.js          Node, zero dependencies
      │  HTTP POST 127.0.0.1:<port>/call  + X-Nextra-Token
      │  (port/token read from %APPDATA%/Nextra/server-<pid>.json)
      ▼
 Nextra.Host.rhp  (inside Rhino)  server thread → Dispatcher → Rhino UI thread
      │  reflection call Entry.Call(tool, argsJson, state)
      ▼
 Nextra.Engine.dll (hot-reloaded) view · query · run · gh_edit · undo · memory
      │  RhinoCommon / Grasshopper / RhinoCode APIs
      ▼
 Rhino document + Grasshopper document
```

Every call returns `{ok, text, images[]}`. The `text` is what the LLM reads, so the whole design aims to keep it short, exact and actionable.

## Design rules the code follows

1. **Text first, cheapest level first.** Summaries come before detail; vertices and full data only on request.
2. **Short handles, never GUIDs.** `r12` is a Rhino object and `g4` is a GH object.
3. **Every edit reports its own effect** (a delta), so the LLM never has to re-list the model to check.
4. **Every edit is reversible** through a checkpoint `cN`.
5. **Errors are answers.** A failure says what failed, why, and what to do next. No raw stack traces.
6. **Never block Rhino and never guess.** Calls have timeouts, dialogs are named, and with several Rhino instances the bridge refuses until one is chosen.
7. **The host is thin and the engine is hot-reloaded**, so almost all changes need only a rebuild, not a Rhino restart.
