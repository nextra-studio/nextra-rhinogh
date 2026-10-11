# Test cases — results (2026-10-11/12, Rhino 8 SR35, live)

Every case was run through the real tools against a running Rhino; images are the exact images the LLM received.

| # | Case | Result | Evidence |
|---|---|---|---|
| 01 | Rhino from scratch (frame, 3 storeys) | ✅ one `run`, delta aggregated by layer/type/size | `01_rhino_from_scratch/`, `02_rhino_modify/frame.3dm` |
| 02 | Rhino modify: add a storey, move top storey +3 m | ✅ library script + args (14 tok in); `q('bbox:…')` move; undo restores exactly | `02_rhino_modify/` |
| 03 | GH from scratch (twisting tower) | ✅ 14/14 lines, 1 Brep; ambiguous names resolved by ports | `03_gh_from_scratch/` |
| 04 | GH modify one part (rectangle → circle) | ✅ 3 lines, placed inside its group, nothing else moved | `04_gh_modify/` |
| 05 | Rhino geometry ↔ GH | ✅ slab referenced into GH (`ref`), fins built, baked back (`r73-r96`) | `05_rhino_gh_mixed/` |
| 06 | GH Python 3 component | ✅ declared ports, code, stdout; error → `[line:col]` with context → 20-token patch | `06_gh_python/` |
| 07 | GH C# component | ✅ ports from `RunScript` signature (type hints, list/tree, ref outputs) | `07_gh_csharp/` |
| 08 | Python component with Karamba-style radio buttons | ✅ custom attributes (needs `IScriptAttribute` in Rhino 8), click → choice, survives rebuild | `08_radio_buttons/radio_component.py` + images |
| 09 | Save Rhino python | ✅ `save_as` → library with docstring index | memory library |
| 10 | Use existing python in a folder | ✅ `run {lib, args}`; `library_dirs` folders and `<3dm>/scripts` indexed | memory library |
| 11 | Compare with existing MCPs | ✅ ≈7× fewer tokens than McNeel, ≈15× fewer than RhinoMCP | `11_compare/REPORT.md` |
| 12 | Karamba simple model | ✅ 47.04 cm = hand calc PL³/48EI; dropdown via `$state`, supports via check-boxes | `12_karamba/` |
| 13 | Marina Bay Sands (Rhino) | ✅ one script, saved to library | `13_marina_bay_sands/` |
| 14 | House solids → centre lines/mid-surfaces → Karamba | ✅ 14 lines + 6 shells snapped to levels/axes, concrete sections, mass 99.6 t (hand check 99.5 t) | `14_house_to_fe/` |
| 15 | GH work shorthand cannot express → python | ✅ explicit data tree via `run`, GH delta reported in shorthand | `15_gh_python_fallback/` |
| 16 | Messy spaghetti → tidy | ✅ regroup in one call, layered layout, inputs ordered by consumer; optional `_In/_Out` relays | `16_messy_tidy/` (before/after) |
| + | Component controls (check-box, right-click menu, click) | ✅ Karamba Support Tx…Rz, Bounding Box "Union Box", real click | `extra_component_controls/` |
| + | Status dot | ✅ Rhino status-bar corner (amber), GH canvas corner (green) | `status_dot/` |
| + | Memory | ✅ prefs, mistake log, compact grouping, rules cap, session list | `memory_test/` |

Robustness events met during testing (all handled by returning text, never hanging the client): Karamba modal at GH load (timeout → dialog named → dismiss → late result), GH "File Saving Error" box (now: no-UI save), Hops' headless compute loading the plug-in (now: headless guard + per-instance discovery), parallel MCP calls (now queued).
