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
- [x] 11 Compare vs existing MCPs (tokens + steps) → `test/v1/11_compare/` (v1) and `test/v2/REPORT.md` (v2)
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
- [x] F3 GitHub push — private repo https://github.com/Tatsuki0123/nextra-rhinogh (2026-10-12)

## H. Re-verification + additions (2026-10-12) — resume point after a usage-limit pause
State: Rhino running with dev build; old Nextra-gh/-rhino packages are in %APPDATA%/Nextra/disabled_packages (restore at the end);
config.json has a test memory_dir (restore from config.json.bak at the end); user's library/frame_building.py still has the L1-naming bug (fix copy from test/regress/scripts).
- [x] H1 Regression suite `test/regress/regress.py` (all cases, one session, checks) — pass 2: 150/157
- [x] H2 Fixed: dialog probe blocked on UI thread (timeouts/status hung), WPF dialog text+dismiss, run error line numbers (prelude offset), undo error order, GH moves in run delta, results follow data to end nodes, round-trip (`gN = …` paste into new canvas), $Expression duplicate, unused x/y on new py scripts
- [x] H3 API reference: Core/ApiDocs.cs, `query {scope:'api'}` (RhinoCommon + GH SDK + rhinoscriptsyntax, cleaned, ranked, cached per Rhino version, Markdown in %APPDATA%/Nextra/api/md)
- [x] H4 User folder %APPDATA%\Nextra\user (plug-in creates it at first start with README + templates; Rhino command NextraFolder opens it): preferences.md, workflows/ (`when:` line), plugins/ (`plugin:` line + `## Rules`), scripts/. Wired into memory list, docs, library, plug-in guides, bridge schema, SKILL.md. TODO (H8): dev docs + README mention; package a skill .zip for claude.ai upload
- [x] H5 Regression pass 3 → all 177 steps pass (host: InternalGetWindowText so status/timeouts never wait for a busy UI thread; round-trip id collision; rs docs order)
- [x] H6 Code review (engine + host) → fixed: per-document handle tables, exact layer/name/group selectors (+ `inside:`), symbol ports (`g10.<`) + XZ/YZ plane literals round-trip, mirror file names per document, plug-in rules reset per conversation, GH solutions-disabled respected on open, StateIO bool parsing, menu disposal, capture pruning (7 days), server stop never re-publishes a dead port / no duplicate doc hooks, engine reload keeps the old engine if the new dll fails. Regression 194/194 on a fresh Rhino, and 194/194 again in the same session.
- [x] H7 Benchmark v2 (test/v2, REPORT.md): A office 548 objects + edits + undo, B attractor facade + 3 edits, C organic gridshell + 2 edits; output / input / fixed / billed tokens per task; all 9 system×scenario results checked correct. Whole session: Nextra ≈9.1k, McNeel ≈32.9k (3.6×), RhinoMCP ≈64.9k (7.1×); billed input no cache 122k / 625k / 1.06M. Found + fixed: results through Custom Preview, false conversion notes, phantom checkpoint ids.
- [x] H8 Environment restored (old Nextra-gh/-rhino packages back, RhinoMCP uninstalled, test memory_dir removed from config, user's frame_building.py naming fixed with .bak), README / dev docs / test README updated, claude.ai skill zip in package.ps1. Final regression in the normal environment: 194/194.

## I. v3 — all cases again + three-way comparison on every case (2026-10-12) — resume point
Scope agreed with the user: every v1 case 01–16 on all three systems (Nextra, McNeel Rhino-MCP-Platform, RhinoMCP) where the
system can do it at all ("not possible" + reason otherwise), plus 4 new cases: 17 freeform Rhino, 18 GH data trees,
19 large-model inspection, 20 Karamba shell. Input/output tokens per case, correctness checked neutrally. Results in test/v3/.
During the run: RhinoMCP installed temporarily (uninstall at the end), McNeel MCPStart on :5123.
- [x] I1 Nextra regression from scratch: 194/194 (component search tightened: suffix-stripped exact names, weak fuzzy matches dropped — 830 → 300 tokens on a 6-name lookup)
- [x] I2 New cases 17–20 on Nextra, added to the regression suite as c24–c27 (canopy, data trees, 1,224-object model, Karamba shell 2.90 cm vs plate theory 2.84 cm)
- [x] I3 v3 harness test/v3 (bench_v3.py, neutral specs3.py emitted per system, pyhelpers.py for competitor Python fallbacks)
- [x] I4 All 19 cases × 3 systems correct (identical checks). Whole session: Nextra 16.2k tokens / 43 calls / 0 fallbacks; McNeel 77.0k (4.8×) / 108 / 34; RhinoMCP 237.7k (14.7×) / 96 / 32
- [x] I5 test/v3/REPORT.md + tables_v3.md; README / test index / dev docs
- [x] I5b User-reported: wires drawn from (0,0) after `$state` on a wired component (Read() leaves unresolved proxy sources) → StateIO relinks; regression c27 + dangling-wire count in every v3 GH check
- [x] I6 Regression 238/238, commit + push (RhinoMCP uninstall: next time Rhino is closed)

## J. `.ghs` file format (2026-10-12)
User request: a new file format that is the shorthand of a definition, documented and used in the repo.
- [x] J1 `gh_edit {save:'x.ghs'}` / `{open:'x.ghs'}`; Writer file mode (canonical ids, full code, Python hints, positions, all states, registered names, Rhino refs as object ids); Builder `pos`, fresh groups per section; parser `\"""` escape
- [x] J2 Round trips identical + same results: kshell, mixed scripts test, v1 house FE, messy, tidy, relays (first save of an old-Karamba file normalises port names once)
- [x] J3 Found on the way: `pos` must move Karamba's hidden params (stretched components); component search preferred `Domain` over `Domain²` (verbatim names now win)
- [x] J4 Repo uses it: test/v3/scripts/*.ghs, regress/scripts/ghs_mix.ghs, .ghs next to the v1 .gh files; dev/04_shorthand.md §7, skill shorthand.md, bridge schema; regression c28

## K. Tool audit and repair (2026-10-13)
- [x] K1 Reproduced prior findings with `test/regress/edge_cases.py`; retained original failing images/assertions.
- [x] K2 Bridge: enforce Ask-mode call restrictions, pin implicit instance selection, health heartbeat preserves late output; deterministic bridge checks.
- [x] K3 Vision: GH handle/cell disambiguation, viewport name/target/CPlane restoration, projected-bounds crop and cell inclusion, tags-off diagnostics.
- [x] K4 Shorthand/search: XY/XZ/YZ literals before references; spelling-based component ranking, no unrelated category-only matches.
- [x] K5 Undo: layer-only checkpoints; layer name/colour, nested/current layer and bake-created layer restoration; dialog popup selection and close verification.
- [x] K6 Full regression 239/239 twice in one session; focused audit 26/26; both builds and generated plugin/archive checks pass. User-docs and learned memory isolated during regression, config restored.
- [x] K7 Detailed [report with before/after images](reports/2026-10-13-tool-audit.md); focused commits pushed to main; no release publication.
