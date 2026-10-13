---
name: nextra-rhinogh
description: Drive Rhino 8 and Grasshopper through the Nextra MCP tools (view, query, run, gh_edit, undo, memory). Use for any Rhino modelling, geometry inspection, Grasshopper definition building/editing/tidying, GH script components, Karamba models, or running/saving Rhino Python scripts.
---

# Nextra — Rhino + Grasshopper

Six tools. The engine inside Rhino guarantees correctness (handles, checkpoints, layout, errors); you decide *what* and *where*.

## Several Rhino instances
The bridge binds a session to one Rhino. If more than one is open it refuses until you choose: `query {scope:'instances'}` → `query {instance:'house.3dm'}` (file name or pid; sticky). Or set `NEXTRA_FILE` in the MCP config. It never falls back to another instance.

## Session start
1. `memory {action:'list'}` → the user's own preferences and workflow list (from their Nextra folder), learned rules, script library. **Obey them; they override this skill.** If a listed workflow's `when:` matches the request, read it first: `query {scope:'docs', filter:'workflows/<name>'}`.
2. Look before you edit: `query` (text, cheap) then `view` only when shape/position matters.
3. **Keep preferences up to date without being asked**: when the user states a standing preference (units, layers, naming, colours, style, "always/never …") or corrects the same thing twice, `memory {action:'save_pref', text:'key: value'}` and say so in one line ("saved preference: …"). Same `key:` = the old line is replaced, so preferences stay current; to drop or merge several, `set_prefs {text}` with the full list. Workflows: `save_workflow` (name, when, text) when the user asks. These write to the user's folder (Rhino command `NextraFolder` opens it).
4. Plug-in guide wanted ("make a guide / rules for Kangaroo") → write `plugins/<Category>.md` **in this skill folder**, following `plugins/_TEMPLATE.md` (Category = component category from `query {scope:'components', filter:'@Name'}`). If the skill folder is read-only (skill uploaded as a .zip on claude.ai / Claude Desktop), use `memory {action:'save_guide', name:<Category>, text}` instead — same format, stored in the user's folder.

## Priority (always)
1. **GH canvas work → `gh_edit` shorthand** (`shorthand.md`).
2. **A library script fits → `run {lib:'name', args}`** (`query {scope:'library'}` lists them; user folders included).
3. **Otherwise → `run` code** (Python 3 default; `cs` for C#). Fixes are `patch:[{find,replace}]`, never a full resend.
   Unsure of an API? Search the installed version's reference instead of guessing: `query {scope:'api', filter:'loft curves'}` (ranked one-liners) → `filter:'Brep.CreateFromLoft'` (full doc) · `filter:'Brep'` (type overview) · `'rs.AddLoftSrf'` · `'src:gh GH_Document'`.

## Handles & regions
- Rhino objects are `r12`, GH objects `g4`, GH groups `g9` too. Session-stable; new objects get new numbers. Use them everywhere (filters, `h('r12')` in code, shorthand refs).
- Focus uses lowercase `g1`–`g8` for GH handles and uppercase `G1`–`G8` for grid cells. Cell ranges such as `C3:E5` are unambiguous.
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
- `plugins/<Category>.md` — plug-in guides (Karamba3D, …) + `_TEMPLATE.md` for new ones. **Before using a plug-in's components, read its guide here** if one exists. When the skill is installed as a folder (Claude Code: `~/.claude/skills/nextra-rhinogh`), the plug-in also attaches the guide's `## Rules` automatically the first time those components appear, once per session (`hint` = pointer only, `off` — config `"plugin_docs"` or `gh_edit {plugin_docs:'hint'}`). Guides in the user's folder (`save_guide`) win over the ones here.
- The other docs are also served by the plug-in (`query {scope:'docs'}`), so they work even when this skill was uploaded as a .zip.
