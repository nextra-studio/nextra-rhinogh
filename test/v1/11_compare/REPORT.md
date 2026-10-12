# Case 11 — Nextra vs existing Rhino/GH AI tools (token cost and capability)

Date: 2026-10-11 · Rhino 8 SR35 · all three MCP servers running **live in the same Rhino session on the same documents**.
Token estimate: text = chars / 4, image = width × height / 750 (Anthropic's image formula). Raw requests/responses: `out/`, harness: `bench_*.py`.

## 1. Fixed cost per session (tool definitions loaded before any work)

| System | Tools | Schema tokens (measured from `tools/list`) |
|---|---|---|
| **Nextra** (this repo) | 6 | **≈ 1,300** |
| McNeel Rhino-MCP-Platform 0.1.5 | 29 | ≈ 4,780 |
| RhinoMCP 0.4.1 (jingcheng-chen) | 70 | ≈ 22,900 |

## 2. Same tasks, minimal call sequence on each system (in + out tokens)

Each task is done the way an LLM must do it on that system **to reach the same information** (e.g. if a create call does not say what it created, the follow-up listing is counted).

| Task | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| T1 model overview (36 objects) | **106** | 929 (GUID list, no sizes) | 876 |
| T2 one member in depth | **97** (oriented box, topology, faces by normal, area, volume) | 48 name/GUID only → + `run_python` for sizes ≈ 170 *(est.)* | failed (duplicate names); by id ≈ 75, world bbox only |
| T3 image 800 px | 683 (grid + object tags, occlusion-aware) | 610 (no tags) | 480 (no tags) |
| T4 build 3-storey frame by code **and know what was made** | **586** (delta: 3 lines by layer/type/size, handles) | 533 + listing ≈ 3,250 *(est. from T1 rate)* ≈ 3,780 | 533 + `get_objects` 3,758 (only 50 of 90) ≈ 4,290 |
| T5 add one storey | **121** (library script + args) | 533 (resend code) | 533 (resend code) |
| G1 GH twisting tower, built & verified | **261, 1 call** | 7,423, 7 calls (2 searches, ambiguity recovery, python for flatten/groups, canvas read) | 7,899, 2 calls (first failed); no flatten/groups |
| G2 modify: rectangle → circle | **92, 1 call** | 2,681, 2 calls (python: no delete/expression tool) | ≥ 7,400 *(est.: mutate + read-back)* |
| G3 read GH canvas back | **164** (shorthand) | 3,037 (JSON) | 7,423 (JSON) |

### Whole scenario (schema + T1–T5 + G1–G3)

| | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| Total tokens | **≈ 3,400** | ≈ 24,000 | ≈ 52,000 |
| Ratio | 1× | **≈ 7×** | **≈ 15×** |
| Tool calls | 10 | 17 | ≈ 13 (+ failures) |

Where the difference comes from:
- **Handles instead of GUIDs** (`r12`, `g4`): every McNeel/RhinoMCP listing repeats 36-character GUIDs.
- **Deltas instead of re-listing**: Nextra's `run`/`gh_edit` reply says what changed (aggregated by layer/type/size), so no verification listing is needed.
- **Shorthand both ways**: the same 16-node GH graph is 138 tokens of shorthand to write and 156 to read back, versus 3,000–7,400 tokens of JSON.
- **Library + patches**: repeated work is `run {lib, args}` (14 tokens), fixes are `patch` (≈ 20 tokens), not resends.

## 3. Capability comparison

| | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP | Raven V2* |
|---|---|---|---|---|
| Interface | MCP (6 tools) + skill docs | MCP (29 tools) | MCP (70 tools) | in-GH chat panel, own cloud LLM |
| Undo of agent changes | **checkpoint per call, exact Rhino + GH restore** | — | undo/redo of Rhino stack | — |
| Rhino geometry understanding | **oriented box + shape class, levels summary→verts** | name/type/layer | world bbox | n/a |
| Vision | **grid + handle tags, region zoom, occlusion, GH canvas render, GH preview in Rhino** | viewport JPG + camera metadata | viewport PNG | reference image input |
| GH build | shorthand, auto-resolve ambiguous names by ports | graph JSON (explicit X/Y) | graph JSON + layout | natural language |
| GH component settings | **inputs, flags, expressions, `$state` (dropdowns, on-component check-boxes), right-click `menu=`, `click=`** | sliders only | values (failed for generic inputs) | partial |
| Script components | create/edit Py3/C#/IronPython, **ports from C# signature**, errors with lines | — | — | generates |
| Layout | Sugiyama micro/macro, in-place group re-layout, tidy, interface relays | caller positions | max_columns wrap | auto |
| Plug-in knowledge | live ComponentServer + **plug-in guides auto-attached** (Karamba3D) | search | search | trained |
| Robustness | timeout + late result, dialog text + dismiss, per-line errors, headless guard, instance binding | per-step status | errors as text | — |
| Learning | prefs, mistakes log → approved rules, script library | — | — | — |

\* Raven V2 is not an MCP server (chat UI inside Grasshopper with its own model and per-day token quota), so it cannot be driven by Claude and token cost is not comparable; its row is from public descriptions ([datadrivenaec](https://datadrivenaec.com/tools/raven), [iarchway tutorial](https://iarchway.com/en/grasshopper_ai_ra/), [myarchitectai review](https://www.myarchitectai.com/blog/grasshopper-ai)).

## 4. Honest caveats
- One run per task, one document; numbers are representative, not statistical.
- Images: Nextra's default (800×600) is slightly larger than the others' (800×450); per pixel the cost is the same.
- RhinoMCP G2 is an estimate (its build already cost 6.5k; a mutate + read-back cannot be smaller than the read-back measured in G3).
- McNeel/RhinoMCP may improve with prompt tricks (e.g. smaller listing limits); the comparison uses their documented defaults.
