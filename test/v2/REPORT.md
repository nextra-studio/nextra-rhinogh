# Nextra vs other Rhino / Grasshopper AI tools: tokens and capability

Date: 2026-10-12 · Rhino 8 SR35 · all three MCP servers running live in the same Rhino.
Systems:
- **Nextra:** this repo.
- **McNeel Rhino-MCP-Platform 0.1.5:** McNeel's own server.
- **RhinoMCP 0.4.1:** jingcheng-chen's server.

Raven V2 is covered in the capability table only (§4), because it is not an MCP server.

## 1. Method (v2, detailed tasks)

**What is counted**
- **Output tokens:** what the model writes, i.e. the arguments of every tool call (code, shorthand, graph JSON).
- **Input tokens from results:** what the model reads back, i.e. the tool results (text and images).
- **Fixed context:** what the model holds in every request: tool schemas plus server instructions, plus the skill document for Nextra (loaded once). Nextra also pays for reading the shorthand guide (1.5k tokens) in scenario B, counted as a normal call.
- **Billed input:** each request re-sends the whole conversation, so the input billed across a scenario is far larger than its unique tokens. Two estimates are given:
  - without prompt caching;
  - with prompt caching, where the repeated prefix costs 10%.

**How it was run**
- **Same work on every system.** Each scenario is described once, neutrally. Every system receives it in its own format (shorthand, McNeel's graph JSON, RhinoMCP's graph JSON), so all carry the same content.
- **Minimal working path.** Each system uses the fewest calls a competent model would need.
- **Competitors are helped, not penalised:**
  - Their scripts print a compact "what was made" summary themselves, so they never pay for a full object listing.
  - Formats their schemas do not document were used correctly from the start, without failed attempts. Examples: RhinoMCP's group colours as integer arrays, its slider-instead-of-literal for generic inputs, and its separate value call.
- **Real costs are kept.** Recovery calls an API forces are counted. Example: McNeel rejects ambiguous component names and lists candidate GUIDs, so re-placing those components and re-wiring them is counted.
- **Correctness is checked neutrally after every scenario.** Object counts, positions, groups, the final geometry count and the undo result are checked for each system, outside the token count. **All 9 system × scenario runs produced the correct model.**

**Estimation and evidence**
- Tokens are estimated as text ÷ 4 characters and image width × height ÷ 750, the same for every system. One run.
- Harness: [bench_v2.py](bench_v2.py) and [ghspec.py](ghspec.py). Raw requests and replies: `out/<scenario>/<system>/`. Numbers: `rows_v2.json`, tables: `tables_v2.md`.

## 2. Results

### Whole session (A + B + C in one conversation)

| | tool calls | output tokens | input tokens from results | fixed context | **total** | billed input, no cache |
|---|---|---|---|---|---|---|
| **Nextra** | 18 | 1,619 | 4,739 | 2,757 | **9,115** | 121,565 |
| McNeel Rhino-MCP-Platform | 34 | 7,990 | 20,152 | 4,779 | **32,921** (3.6×) | 625,202 (5.1×) |
| RhinoMCP | 23 | 5,032 | 36,885 | 23,029 | **64,946** (7.1×) | 1,059,397 (8.7×) |

### Fixed context, paid in every request

| | tool schemas | server instructions | skill | total |
|---|---|---|---|---|
| Nextra | 1,406 | 91 | 1,260 | **2,757** |
| McNeel | 4,779 | 0 | — | **4,779** |
| RhinoMCP | 22,935 | 94 | — | **23,029** |

### A · Rhino: 4-storey office with 548 objects, then edits

The office has a column grid, beams, slabs with a core opening, core walls, stairs and facade mullions. The edits are: add a storey, move the core 3.6 m, delete the south facade, undo.

| | calls | output | input from results | fixed | **total** | billed, no cache | billed, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 8 | 969 | 1,728 | 2,757 | **5,454** | 41,172 | 9,025 |
| McNeel | 7 | 2,027 | 1,269 | 4,779 | **8,075** | 56,083 | 12,875 |
| RhinoMCP | 7 | 2,034 | 2,494 | 23,029 | **27,557** | 208,076 | 45,608 |

| task: output / input (calls) | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| session start (prefs, rules, library) | 4 / 193 | — | — |
| build the office (same script everywhere) | 891 / 171 | 934 / 49 | 937 / 65 |
| look (image) | 3 / 735 | 19 / 774 | 14 / 642 |
| inspect the core walls | 6 / 341 | 4 / 374 | 6 / 1,682 |
| add a storey | **13** / 191 (saved script + args) | 940 / 49 (resend code) | 943 / 66 (resend code) |
| move the core | 28 / 61 | 61 / 9 | 64 / 16 |
| delete the south mullions | 19 / 24 | 64 / 9 | 67 / 18 |
| undo | 5 / 12 | 5 / 5 | 3 / 5 |

**Reading it honestly.** For plain Rhino scripting the gap is moderate, and comes from three things:
- **Smaller fixed context.**
- **Library reuse.** Re-running a saved script costs 13 output tokens instead of ~940 for resending it.
- **Selection helpers.** `q('layer:Core')` replaces hand-written loops.

Nextra reads *more* after the build (171 vs 49 tokens). Its reply describes what was made: counts by layer, type and size, plus handles. The competitors' scripts only printed per-layer counts.

### B · Grasshopper: attractor facade, then edits

The facade has 27 components in 5 groups. The edits are: replace the attractor point with a line, add a slider scaling the openings, tidy the canvas.

| | calls | output | input from results | fixed | **total** | billed, no cache | billed, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 6 | 371 | 2,272 | 2,757 | **5,400** | 31,976 | 8,057 |
| McNeel | 14 | 2,955 | 8,670 | 4,779 | **16,404** | 177,868 | 32,550 |
| RhinoMCP | 6 | 1,564 | 19,942 | 23,029 | **44,535** | 253,711 | 65,452 |

| task: output / input (calls) | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| read the GH grammar once | 10 / 1,514 | — | — |
| find ambiguous components | — | 8 / 326 | 8 / 354 |
| build, all 27 components wired and grouped | **270 / 191** (1) | 956 / 1,101 | 1,080 / 11,477 |
| recover from ambiguous names | — | 478 / 626 (2) | — |
| literals, groups, colours by script | — | 610 / 7 | — |
| read the result back | — (in the build reply) | 10 / 5,274 | — (in the build reply) |
| attractor point → line | **49 / 76** (1) | 404 / 400 (3) | 320 / 4,236 (1) |
| add opening-scale slider | **34 / 49** (1) | 252 / 158 (3) | 156 / 1,751 (1) |
| tidy | **4 / 25** | 218 / 7 (hand-written layout script) | 0 / 1,482 |
| look | 4 / 417 | 19 / 771 | 0 / 642 |

### C · Grasshopper: organic gridshell, then edits

The gridshell is built from three arches with a wave crown, lofted, divided into a U×V grid of members and piped (27 components). The edits are: change the wave height and grid density, add edge beams.

| | calls | output | input from results | fixed | **total** | billed, no cache | billed, prompt cache |
|---|---|---|---|---|---|---|---|
| Nextra | 4 | 279 | 739 | 2,757 | **3,775** | 16,389 | 5,036 |
| McNeel | 13 | 3,008 | 10,213 | 4,779 | **18,000** | 160,692 | 32,269 |
| RhinoMCP | 10 | 1,434 | 14,449 | 23,029 | **38,912** | 356,160 | 70,636 |

| task: output / input (calls) | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| find ambiguous components | — | 24 / 604 (3) | 24 / 764 (3) |
| build | **250 / 225** (1) | 974 / 1,558 | 1,134 / 11,667 |
| recover / fix / read back | — | 1,702 / 7,157 (4) | 147 / 11 (1) |
| change wave and density | **7 / 31** | 86 / 7 | 15 / 81 (2) |
| add edge beams | **18 / 67** (1) | 203 / 116 (3) | 114 / 1,284 (2) |
| look | 4 / 416 | 19 / 771 | 0 / 642 |

### Why the gap is largest in Grasshopper

1. **Shorthand instead of graph JSON.** The same 27-component graph is about 250 tokens of shorthand versus 950–1,130 tokens of JSON. JSON repeats keys, GUIDs, port indices and canvas positions.
2. **The reply already says what happened, briefly.**
   - **Nextra:** about 200 tokens with ids, problems and results.
   - **McNeel:** its build reply has no data, so the model must read the canvas back (5–6k tokens).
   - **RhinoMCP:** its reply contains everything (11k tokens per build).
3. **Ambiguity is resolved by the engine.** Nextra picks between same-named components using the ports on the line, for example Rectangle, Merge or Addition. McNeel rejects the name and makes the model search and re-place the component. RhinoMCP needs GUIDs, which means a search call per ambiguous name.
4. **Edits are diffs.** A change is a few lines. Literal values, flatten flags, groups, colours and layout are part of the same grammar instead of follow-up scripts.
5. **Fixed context.** RhinoMCP's 70 tool schemas (23k tokens) ride along in every request. That is why its billed input reaches a million tokens over one session.

### Results of the earlier, simpler benchmark (v1)

The v1 tasks were a 36-object frame and a 16-node twisting tower, with output and input counted together and without billing estimates. Nextra came to about 3.4k tokens, McNeel about 24k and RhinoMCP about 52k. The raw data is in [../11_compare/](../11_compare/).

## 3. Things that went wrong on the way (all systems)

**McNeel**
- `g1_apply_graph` rejected `Construct Point`, `Area`, `Scale`, `Remap Numbers`, `Division` and others as ambiguous and placed nothing for them. The model has to re-place them by GUID and re-wire them by instance id.
- Literal inputs, flatten flags and groups need a Python script. That script must call `NewSolution(True)`, or changed inputs are not recomputed.

**RhinoMCP**
- Undocumented details: group colours must be integer arrays, and literal values are refused on generic-type inputs.
- `gh_mutate_graph` has no value operation, and `gh_set_parameter_value` ignored `input_name` (the index worked).
- These were not charged to RhinoMCP; the benchmark used the working form from the start.

**Undo, McNeel and RhinoMCP**
- Both use Rhino's own undo stack. In one long-lived Rhino session it answered "Nothing to undo" for both.
- In a fresh session both undid correctly. That is the run reported above.
- A separate probe ([undo_probe.py](undo_probe.py)) showed that Nextra's activity (queries, runs, GH edits) does not disturb other tools' undo.
- Nextra's own `undo` restores from its checkpoints, so it does not depend on that stack.

**Nextra**
- The benchmark found and fixed four Nextra bugs:
  - `results:` was hidden when the definition ended in a Custom Preview.
  - "may need conversion" notes appeared for wires that Grasshopper converts fine.
  - `run` printed a checkpoint id it had just discarded, in both `run` and `gh_edit`.
  - A loft through rational arcs produced a polysurface. This was a modelling issue, fixed in the spec with Loft Options for every system.

## 4. Capability comparison

| | Nextra | McNeel Rhino-MCP-Platform | RhinoMCP | Raven V2* |
|---|---|---|---|---|
| Interface | MCP (6 tools) + skill | MCP (29 tools) | MCP (70 tools) | in-GH chat panel, own cloud model |
| Undo of agent changes | **checkpoint per call; exact Rhino + GH restore, independent of Rhino's undo stack** | Rhino `_Undo` via run_command | undo/redo of Rhino's stack | — |
| Says what changed | **Rhino delta (by layer, type and size, with handles) + GH delta + GH results** | script stdout only | script stdout; GH replies list everything | — |
| Rhino geometry understanding | **oriented box + shape class; summary → list → detail → vertices** | name, type, layer | world bbox, full geometry dump | n/a |
| Vision | **grid + handle tags, region zoom, occlusion, GH canvas render, GH preview in Rhino** | viewport image + camera | viewport image, GH preview capture | reference image input |
| GH build | shorthand; ambiguous names resolved by ports | graph JSON (explicit X/Y); ambiguous names rejected | graph JSON + layout, aliases | natural language |
| GH edit | shorthand diff (delete, rewire, insert into group) | place/connect, plus Python for removals | `gh_mutate_graph` ops | — |
| Component settings | **literals, flags, expressions, `$state` (dropdowns, on-component check-boxes), right-click `menu=`, `click=`** | sliders | values (not on generic inputs) | partial |
| Script components | Py3 / C# / IronPython; ports from the C# signature; errors with lines | — | — | generates |
| Layout | Sugiyama micro/macro, in-place group re-layout, tidy, interface relays | caller positions | wire-based layout | auto |
| API reference | **`query scope:'api'`: RhinoCommon + Grasshopper SDK + rhinoscriptsyntax from the installed version, ranked, cleaned** | — | rhinoscriptsyntax docs | trained |
| Plug-in knowledge | live component search + **plug-in guides auto-attached** (Karamba3D) | component search | component search | trained |
| User instructions | **user folder: preferences, workflows (`when:`), plug-in guides, scripts; works with an uploaded skill** | — | modeling guidance topics | — |
| Robustness | timeout + late result; dialog text + verified dismiss; per-line errors; headless guard; instance binding | per-step status | errors as text, rollback | — |
| Learning | prefs, mistakes log → user-approved rules, script library | — | — | — |

\* Raven V2 is not an MCP server: it is a chat UI inside Grasshopper with its own model and a per-day token quota. It cannot be driven by Claude, so its token cost is not comparable. Its row comes from public descriptions: [datadrivenaec](https://datadrivenaec.com/tools/raven), [iarchway tutorial](https://iarchway.com/en/grasshopper_ai_ra/), [myarchitectai review](https://www.myarchitectai.com/blog/grasshopper-ai).

## 5. Caveats

- **One run per task.** Numbers are representative, not statistical. The model's own reasoning text is not counted; it would be similar across systems.
- **Token estimates.** chars/4 approximates Claude's tokenizer and is applied equally to everyone.
- **The competitors' minimal paths were written by me.** A model might find shorter ones, for example smaller verify reads. It might also need longer ones: trial and error on undocumented formats was not charged.
- **The billing estimates are simple models** of request re-sending. Real bills depend on the client, cache lifetime and how much the model writes between calls.
