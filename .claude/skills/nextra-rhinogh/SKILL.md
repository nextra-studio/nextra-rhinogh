---
name: nextra-rhinogh
description: Drive Rhino 8 and Grasshopper through the Nextra MCP tools (view, query, run, gh_edit, undo, memory). Use for any Rhino modelling, geometry inspection, Grasshopper definition building/editing/tidying, GH script components, Karamba models, or running/saving Rhino Python scripts.
---

# Nextra — Rhino + Grasshopper

Six tools. The engine inside Rhino guarantees correctness (handles, checkpoints, layout, errors); you decide *what* and *where*.

## Several Rhino instances
The bridge binds a session to one Rhino. If more than one is open it refuses until you choose: `query {scope:'instances'}` → `query {instance:'house.3dm'}` (file name or pid; sticky). Or set `NEXTRA_FILE` in the MCP config. It never falls back to another instance.

## Session start
1. `memory {action:'list'}` → user prefs, learned rules (obey them), script library index.
2. Look before you edit: `query` (text, cheap) then `view` only when shape/position matters.

## Priority (always)
1. **GH canvas work → `gh_edit` shorthand** (`shorthand.md`).
2. **A library script fits → `run {lib:'name', args}`** (`query {scope:'library'}` lists them; user folders included).
3. **Otherwise → `run` code** (Python 3 default; `cs` for C#). Fixes are `patch:[{find,replace}]`, never a full resend.

## Handles & regions
- Rhino objects are `r12`, GH objects `g4`, GH groups `g9` too. Session-stable; new objects get new numbers. Use them everywhere (filters, `h('r12')` in code, shorthand refs).
- `view` returns an image with an 8×8 grid: columns A–H, rows 1–8. Crowded images come **without tags** and list per-cell counts → zoom: `view {focus:'C3:E5'}` (same camera, that region at higher resolution, tagged). Never compute coordinates from images; use cells, handles or `query`.
- Faded tags with `*` = object is behind other geometry.

## Geometry questions: go deep step by step
`query level:'summary'` (layers × types, extents) → `filter:'layer:X'` (one line per object with oriented box `L×W×H linear|planar|block` + axis) → `level:'detail'` (faces by normal, areas, topology) → `level:'verts'` only if you truly need coordinates. Use oriented boxes to infer members (linear vertical = column, linear horizontal = beam, planar n+z = slab, planar horizontal-normal = wall).

## Every edit returns
`run`: stdout, Rhino delta `+ r20 polysurface solid [Layer] 3x0.4x0.4 linear vertical @(…)`, GH delta, errors with the failing lines, `checkpoint cN`.
`gh_edit`: `ok|partial|FAILED`, `ids: local=gN`, per-line errors with valid ports, runtime errors after solve, `results:` data summary of the sinks.
On partial: fix **only** the failed lines, using the new handles. `undo {checkpoint:'cN'}` reverts exactly.

## When things go wrong
- `timeout`/`busy`/`dialog` replies: read the dialog text, `query {scope:'status'}`; cancel with `dismiss_dialog:true` only if it is safe (tell the user what it said).
- Same error twice → change approach (different component, or `run` fallback). Do not loop.
- Shorthand cannot express it → `run` with `ghdoc()` / `h('g5')` (see `api/gh-python.md`), then report what you did.
- After an error you solved, `memory {action:'log_mistake', text, fix}`; when the tool says "recurring mistake", do it. `memory compact` → show proposed rules to the user → `set_rules` only after approval.

## Docs (read only what you need: `query {scope:'docs', filter:'<name>'}` or open the file)
- `shorthand.md` — GH shorthand grammar + examples (read before the first gh_edit of a session)
- `workflows.md` — recipes: Rhino from scratch / modify, GH build / modify / tidy, Rhino↔GH, scripts, Karamba
- `api/rhino-python.md`, `api/rhino-csharp.md`, `api/gh-python.md`, `api/gh-csharp.md` — cheat sheets
- `plugins/<Category>.md` — plug-in guides (Karamba3D, …). **Loaded only when that plug-in is used**: the first time its components appear in gh_edit/query, the guide's `## Rules` are attached to the reply once per session (mode `auto`; `hint` = pointer only; `off`) — set `"plugin_docs"` in `%APPDATA%/Nextra/config.json` or `gh_edit {plugin_docs:'hint'}`. Add your own in `%APPDATA%/Nextra/memory/plugins/` using `plugins/_TEMPLATE.md`.
