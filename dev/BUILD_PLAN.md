# Nextra RhinoGh — Build Plan & Checklist

Source of truth for scope: `dev/Nextra.zero_RhinoGh_Plan.md` + the user's requests (2026-10-11).
Status legend: [ ] todo · [~] in progress · [x] done & verified live in Rhino

## Decisions (agreed)
- Engine: C# host `.rhp` (server thread + UI dispatcher + dialog probe) + hot-reloadable `Nextra.Engine.dll`.
- 6 tools: `view`, `query`, `run`, `gh_edit`, `undo`, `memory`. Bridge = stdio MCP (`.mcpb`) → 127.0.0.1 token server.
- Docs in English. Old Nextra-gh / Nextra-rhino Yak packages disabled during testing, restored after.
- Comparison targets (case 11): Rhino-MCP-Platform, RhinoMCP (jingcheng-chen), Raven V2 (not MCP) + others if feasible.
- Git repo + private GitHub repo (needs `gh auth login` by user).

## A. Infrastructure
- [x] A1 Host plugin: 127.0.0.1-only server, token, server.json, timeout + late results, dialog detect/dismiss
- [x] A2 Status dot (Rhino status-bar corner + GH canvas corner; green/amber/red; NextraOn/Off; verified by screenshot): minimal movable floating dot (green = client active, amber = idle, red = down; click red → restart/retry ports)
- [x] A3 Engine hot-reload, state bag survives reload
- [x] A4 MCP bridge (stdio) + `.mcpb` manifest + `nx` CLI for testing
- [x] A5 Meaningful replies everywhere: what worked, what failed, why, what to do next; fallback instead of repeated errors
- [x] A6 Reference folder (`dev/reference/`) with copied old code / vision / OBB refs  ✅ copied

## B. Rhino
- [x] B1 Handles r1.. (session), delta replies (added/modified/deleted)
- [x] B2 Efficient geometry query: summary → list (OBB) → detail (faces/edges) → verts; never dump vertices by default
- [x] B3 `run` py/cs via RhinoCode, `h("r3")` helper, patch editing of last code, error with surrounding lines
- [x] B4 Checkpoint/undo for every run (Rhino objects)
- [x] B5 Library: use existing python in a folder (lib dirs incl. project `.nextra/library`), save scripts, run by name
- [x] B6 Vision: offscreen ViewCapture, 8×8 grid, region-first (cell counts, not all tags), focus by cells/handles,
       tags as plain text next to elements following element direction (no dots), overlap avoidance, viewport restored

## C. Grasshopper
- [x] C1 GH open/create without blocking (solutions off while loading, missing plugins listed, editor auto load/hide)
- [x] C2 Shorthand v2 (dense, readable): create + modify ops, port by name/index, inline values, flags, groups
- [x] C3 Shorthand covers component state: sliders/panels/toggles/value lists/script code + generic archive state
       (dropdowns/menu toggles like Karamba) — the old system could not edit these
- [x] C4 Reverse engine: canvas → shorthand (same grammar), grouped readable output, errors inline
- [x] C5 Layout: keep Sugiyama micro/macro (groups); improve crossing order; modification mode places new nodes
       near anchors without moving existing; full tidy for messy files
- [x] C6 Component search from loaded ComponentServer (incl. plugins) with ports — no RAG
- [x] C7 Script components (py3 / C# / IronPython): code via file `.nextra/scripts/<h>.py`, patch, recompute, errors w/ lines
- [x] C8 GH checkpoint (document archive) + undo
- [x] C9 GH canvas vision: offscreen render via GH renderer, grid, g-handle tags, focus by cells/handles
- [x] C10 Unbaked GH geometry view in Rhino (preview bbox zoom, conduit tags, doc untouched)

## D. Skill & memory
- [x] D1 SKILL.md (short entry) + shorthand.md + workflows.md + api cheat sheets + profiles (karamba)
- [x] D2 Memory: prefs.md, rules.md (token-capped, always loaded), mistakes.log (auto + manual), library index
- [x] D3 Recurring-mistake detection → hint to compact into rules (user approves); shorthand itself NOT self-modified
- [x] D4 `query scope:docs` serves the same docs for non-Claude LLMs

## E. Test cases (each: run live, save images + notes in `test/<nn>_<name>/`)
- [x] 01 Rhino from scratch
- [x] 02 Rhino modification (add a storey, move part)
- [x] 03 GH from scratch
- [x] 04 GH modify one part
- [x] 05 Rhino geometry + GH mixed
- [x] 06 GH Python component
- [x] 07 GH C# component
- [x] 08 GH Python component with Karamba-style radio buttons on the component (custom attributes), visually verified
- [x] 09 Rhino python save (library)
- [x] 10 Rhino use existing python in a folder
- [x] 11 Compare vs existing MCPs (tokens + steps) → `test/11_compare/REPORT.md`
- [x] 12 GH Karamba simple model
- [x] 13 Rhino Marina Bay Sands
- [x] 14 Architecture house → centerlines → FE model → GH Karamba analysis
- [x] 15 GH non-shorthand stuff via python fallback
- [x] 16 Messy spaghetti GH → tidy (visual check, better than before)

## G. Added during build (user requests)
- [x] G1 NextraOn / NextraOff commands (server + dots in Rhino and GH)
- [x] G2 Plug-in guides folder (skill/nextra/plugins/<Category>.md, auto-attached Rules once per session; auto|hint|off)
- [x] G3 Component controls: `$state` incl. on-component check-boxes, right-click `menu=`, real `click=` (Karamba Support, Bounding Box Union)
- [x] G4 Plugin component layout repair (Karamba collapsible inputs: stray grips / stretched bounds)

## F. Packaging
- [x] F1 Yak-style package folder (`.rhp` + engine), install script
- [x] F2 README (setup, tools, token comparison summary)
- [ ] F3 GitHub push — waiting for `gh auth login` (local git history complete)

## H. Re-verification + additions (2026-10-12) — resume point after a usage-limit pause
State: Rhino running with dev build; old Nextra-gh/-rhino packages are in %APPDATA%/Nextra/disabled_packages (restore at the end);
config.json has a test memory_dir (restore from config.json.bak at the end); user's library/frame_building.py still has the L1-naming bug (fix copy from test/regress/scripts).
- [x] H1 Regression suite `test/regress/regress.py` (all cases, one session, checks) — pass 2: 150/157
- [x] H2 Fixed: dialog probe blocked on UI thread (timeouts/status hung), WPF dialog text+dismiss, run error line numbers (prelude offset), undo error order, GH moves in run delta, results follow data to end nodes, round-trip (`gN = …` paste into new canvas), $Expression duplicate, unused x/y on new py scripts
- [x] H3 API reference: Core/ApiDocs.cs, `query {scope:'api'}` (RhinoCommon + GH SDK + rhinoscriptsyntax, cleaned, ranked, cached per Rhino version, Markdown in %APPDATA%/Nextra/api/md)
- [x] H4 User folder %APPDATA%\Nextra\user (plug-in creates it at first start with README + templates; Rhino command NextraFolder opens it): preferences.md, workflows/ (`when:` line), plugins/ (`plugin:` line + `## Rules`), scripts/. Wired into memory list, docs, library, plug-in guides, bridge schema, SKILL.md. TODO (H8): dev docs + README mention; package a skill .zip for claude.ai upload
- [x] H5 Regression pass 3 → all 177 steps pass (host: InternalGetWindowText so status/timeouts never wait for a busy UI thread; round-trip id collision; rs docs order)
- [x] H6 Code review (engine + host) → fixed: per-document handle tables, exact layer/name/group selectors (+ `inside:`), symbol ports (`g10.<`) + XZ/YZ plane literals round-trip, mirror file names per document, plug-in rules reset per conversation, GH solutions-disabled respected on open, StateIO bool parsing, menu disposal, capture pruning (7 days), server stop never re-publishes a dead port / no duplicate doc hooks, engine reload keeps the old engine if the new dll fails. Regression 194/194 on a fresh Rhino, and 194/194 again in the same session.
- [x] H7 Benchmark v2 (test/11_compare/v2, REPORT.md): A office 548 objects + edits + undo, B attractor facade + 3 edits, C organic gridshell + 2 edits; output / input / fixed / billed tokens per task; all 9 system×scenario results checked correct. Whole session: Nextra ≈9.1k, McNeel ≈32.9k (3.6×), RhinoMCP ≈64.9k (7.1×); billed input no cache 122k / 625k / 1.06M. Found + fixed: results through Custom Preview, false conversion notes, phantom checkpoint ids.
- [x] H8 Environment restored (old Nextra-gh/-rhino packages back, RhinoMCP uninstalled, test memory_dir removed from config, user's frame_building.py naming fixed with .bak), README / dev docs / test README updated, claude.ai skill zip in package.ps1. Final regression in the normal environment: 194/194.
