---
name: nextra-rhinogh
description: "Drive Rhino 8 and Grasshopper through the NextraRhinoGh MCP tools (view, query, run, gh_edit, undo, memory). Use for any Rhino modelling, geometry inspection, Grasshopper definition building/editing/tidying, GH script components, Karamba models, or running/saving Rhino Python scripts. The tools run on the user's computer: in the Claude app, check the computer link (get_device_info) and tool-search \"nextra\" before concluding they are missing."
---

# NextraRhinoGh — Rhino + Grasshopper

Six tools. The engine inside Rhino guarantees correctness (handles, checkpoints, layout, errors); you decide *what* and *where*.

## Step 0 — connect first, every session (before saying anything about Rhino)
The six tools (`view`, `query`, `run`, `gh_edit`, `undo`, `memory`) run on the user's own computer. In the Claude app the chat runs in the cloud and only receives them after the computer link is checked, so on a first message they are usually missing. That is normal and says nothing about Rhino.
1. **Check the computer link.** Use tool search for `get_device_info` (also try `device`, `computer status`, `remote-devices`) and call it, or call an `enable__…remote-devices…` tool if that is what exists. Its reply lists "Local MCP server: nextra-rhinogh". Skip this only in apps that run locally (Claude Code, Codex, ChatGPT Work).
2. **Load the Rhino tools.** Tool search `nextra` (names may be prefixed, e.g. `mcp__…nextra-rhinogh…__query`). Never call a bare `list`.
3. **First call:** `memory {action:'list'}`.
4. Still no tools → do steps 1–3 once more. Only then tell the user, in one short message, that the Rhino tools did not attach to this chat, and ask them to make sure the Claude desktop app is running on the Rhino computer and to reply "retry". You retry the same steps when they do.
- A call that returns "Rhino is not reachable" / "no running Rhino" is different: the tools work but Rhino (or the plug-in) is not running. Ask the user to open Rhino 8 and check the NextraRhinoGh dot at the bottom-right (green/amber; red = click it).
- Never conclude "I can't reach Rhino" before step 1 and 2 have run. Never switch to a pasted script or command-line instructions on your own; offer them only as an option after asking. Do not search connector directories or use computer use / screenshots to drive Rhino.

## Several Rhino instances
The bridge binds a session to one Rhino. If more than one is open it refuses until you choose: `query {scope:'instances'}` → `query {instance:'house.3dm'}` (file name or pid; sticky). Or set `NEXTRA_FILE` in the MCP config. It never falls back to another instance.

## Session start
1. `memory {action:'list'}` → the user's own preferences and workflow list (from their NextraRhinoGh folder), learned rules, script library. **Obey them; they override this skill.** If a listed workflow's `when:` matches the request, read it first: `query {scope:'docs', filter:'workflows/<name>'}`.
2. Look before you edit: `query` (text, cheap) then `view` only when shape/position matters.
3. **Keep preferences current**: when the user states a lasting preference (units, layers, naming, colours, style, "always/never …") or corrects the same thing twice, use `memory {action:'save_pref', text:'key: value'}`. The same key replaces the old line in the persistent user folder. Keep active preferences within 1500 characters; use `set_prefs` to merge stale lines. Mention a saved preference briefly.
4. **Capture reusable workflows**: after a workflow succeeds and is likely to be reused, save a short `when:` trigger, steps and checks with `memory {action:'save_workflow', name, when, text}`. Read an existing workflow before updating it. Keep one-off details out. These files live in the persistent user folder; `NextraRhinoGhFolder` opens it.
5. **Plug-in knowledge**: keep the bundled `plugins/<Category>.md` guides as reference. Save new or user-specific plug-in rules through `memory {action:'save_guide', name:<Category>, text}` in the user's folder, so reinstalling the skill or plug-in does not erase them. Read the current guide before replacing it; its previous version is backed up.

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
- `plugins/<Category>.md` — bundled plug-in guides (Karamba3D, …). User-specific guides live in the persistent user folder and take priority. The plug-in attaches the matching `## Rules` automatically the first time those components appear in a session (`hint` = pointer only, `off` — config `"plugin_docs"` or `gh_edit {plugin_docs:'hint'}`). Read the full guide only when needed.
- The other docs are also served by the plug-in (`query {scope:'docs'}`), so they work even when this skill was uploaded as a .zip.
