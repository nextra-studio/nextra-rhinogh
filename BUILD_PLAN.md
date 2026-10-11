# Nextra RhinoGh — Build Plan & Checklist

Source of truth for scope: `Nextra.zero_RhinoGh_Plan.md` + the user's requests (2026-10-11).
Status legend: [ ] todo · [~] in progress · [x] done & verified live in Rhino

## Decisions (agreed)
- Engine: C# host `.rhp` (server thread + UI dispatcher + dialog probe) + hot-reloadable `Nextra.Engine.dll`.
- 6 tools: `view`, `query`, `run`, `gh_edit`, `undo`, `memory`. Bridge = stdio MCP (`.mcpb`) → 127.0.0.1 token server.
- Docs in English. Old Nextra-gh / Nextra-rhino Yak packages disabled during testing, restored after.
- Comparison targets (case 11): Rhino-MCP-Platform, RhinoMCP (jingcheng-chen), Raven V2 (not MCP) + others if feasible.
- Git repo + private GitHub repo (needs `gh auth login` by user).

## A. Infrastructure
- [~] A1 Host plugin: 127.0.0.1-only server, token, server.json, timeout + late results, dialog detect/dismiss
- [ ] A2 Status dot: minimal movable floating dot (green = client active, amber = idle, red = down; click red → restart/retry ports)
- [ ] A3 Engine hot-reload, state bag survives reload
- [ ] A4 MCP bridge (stdio) + `.mcpb` manifest + `nx` CLI for testing
- [ ] A5 Meaningful replies everywhere: what worked, what failed, why, what to do next; fallback instead of repeated errors
- [ ] A6 Reference folder (`reference/`) with copied old code / vision / OBB refs  ✅ copied

## B. Rhino
- [ ] B1 Handles r1.. (session), delta replies (added/modified/deleted)
- [ ] B2 Efficient geometry query: summary → list (OBB) → detail (faces/edges) → verts; never dump vertices by default
- [ ] B3 `run` py/cs via RhinoCode, `h("r3")` helper, patch editing of last code, error with surrounding lines
- [ ] B4 Checkpoint/undo for every run (Rhino objects)
- [ ] B5 Library: use existing python in a folder (lib dirs incl. project `.nextra/library`), save scripts, run by name
- [ ] B6 Vision: offscreen ViewCapture, 8×8 grid, region-first (cell counts, not all tags), focus by cells/handles,
       tags as plain text next to elements following element direction (no dots), overlap avoidance, viewport restored

## C. Grasshopper
- [ ] C1 GH open/create without blocking (solutions off while loading, missing plugins listed, editor auto load/hide)
- [ ] C2 Shorthand v2 (dense, readable): create + modify ops, port by name/index, inline values, flags, groups
- [ ] C3 Shorthand covers component state: sliders/panels/toggles/value lists/script code + generic archive state
       (dropdowns/menu toggles like Karamba) — the old system could not edit these
- [ ] C4 Reverse engine: canvas → shorthand (same grammar), grouped readable output, errors inline
- [ ] C5 Layout: keep Sugiyama micro/macro (groups); improve crossing order; modification mode places new nodes
       near anchors without moving existing; full tidy for messy files
- [ ] C6 Component search from loaded ComponentServer (incl. plugins) with ports — no RAG
- [ ] C7 Script components (py3 / C# / IronPython): code via file `.nextra/scripts/<h>.py`, patch, recompute, errors w/ lines
- [ ] C8 GH checkpoint (document archive) + undo
- [ ] C9 GH canvas vision: offscreen render via GH renderer, grid, g-handle tags, focus by cells/handles
- [ ] C10 Unbaked GH geometry view in Rhino (preview bbox zoom, conduit tags, doc untouched)

## D. Skill & memory
- [ ] D1 SKILL.md (short entry) + shorthand.md + workflows.md + api cheat sheets + profiles (karamba)
- [ ] D2 Memory: prefs.md, rules.md (token-capped, always loaded), mistakes.log (auto + manual), library index
- [ ] D3 Recurring-mistake detection → hint to compact into rules (user approves); shorthand itself NOT self-modified
- [ ] D4 `query scope:docs` serves the same docs for non-Claude LLMs

## E. Test cases (each: run live, save images + notes in `test/<nn>_<name>/`)
- [ ] 01 Rhino from scratch
- [ ] 02 Rhino modification (add a storey, move part)
- [ ] 03 GH from scratch
- [ ] 04 GH modify one part
- [ ] 05 Rhino geometry + GH mixed
- [ ] 06 GH Python component
- [ ] 07 GH C# component
- [ ] 08 GH Python component with Karamba-style radio buttons on the component (custom attributes), visually verified
- [ ] 09 Rhino python save (library)
- [ ] 10 Rhino use existing python in a folder
- [ ] 11 Compare vs existing MCPs (tokens + steps) → `test/11_compare/REPORT.md`
- [ ] 12 GH Karamba simple model
- [ ] 13 Rhino Marina Bay Sands
- [ ] 14 Architecture house → centerlines → FE model → GH Karamba analysis
- [ ] 15 GH non-shorthand stuff via python fallback
- [ ] 16 Messy spaghetti GH → tidy (visual check, better than before)

## F. Packaging
- [ ] F1 Yak-style package folder (`.rhp` + engine), install script
- [ ] F2 README (setup, tools, token comparison summary)
- [ ] F3 GitHub push
