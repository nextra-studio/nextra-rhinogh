# Benchmark v3: every case, three systems

Date: 2026-10-12 · Rhino 8 SR35 · all three MCP servers running live in the same Rhino.
Systems:
- **Nextra:** this repo.
- **McNeel Rhino-MCP-Platform 0.1.5.**
- **RhinoMCP 0.4.1.**

**Cases:** every v1 case (01–10, 12–16; case 11 is the comparison itself) plus four new ones:
- 17 freeform canopy
- 18 Grasshopper data trees
- 19 a 1,224-object model
- 20 Karamba shell plate

**Result:** all 19 cases produced the correct, identical model on all three systems. Each was checked neutrally after the case.

## 1. Results

### Whole session (all cases in one conversation)

| | tool calls | of which Python fallback | output tokens | input tokens from results | fixed context | **total** | billed input, no cache |
|---|---|---|---|---|---|---|---|
| **Nextra** | 43 | 0 | 5,232 | 8,166 | 2,756 | **16,154** | 0.43 M |
| McNeel Rhino-MCP-Platform | 108 | 34 | 23,178 | 48,995 | 4,779 | **76,952** (4.8×) | 4.12 M (9.7×) |
| RhinoMCP | 96 | 32 | 20,723 | 193,986 | 23,029 | **237,738** (14.7×) | 11.46 M (27×) |

### Per case: output / input tokens · calls (Python fallback calls)

| case | Nextra | McNeel | RhinoMCP |
|---|---|---|---|
| 01 Rhino frame from scratch | 526 / 818 · 2 | 583 / 795 · 2 (1 py) | 586 / 682 · 2 (1 py) |
| 02 Rhino modify (add storey, move, undo) | 48 / 208 · 3 | 644 / 42 · 3 (2 py) | 648 / 59 · 3 (2 py) |
| 03 GH twisting tower | 162 / 1,674 · 2 | 1,272 / 4,481 · 8 (1 py) | 926 / 19,650 · 6 (1 py) |
| 04 GH modify (rectangle → circle) | 18 / 66 · 1 | 414 / 135 · 3 (1 py) | 363 / 1,657 · 2 (1 py) |
| 05 Rhino slab → GH fins → bake | 229 / 152 · 3 | 1,682 / 3,787 · 8 (3 py) | 1,395 / 20,060 · 6 (3 py) |
| 06 GH Python component (+ error, fix) | 168 / 216 · 3 | 2,050 / 155 · 4 (3 py) | 2,051 / 1,415 · 4 (3 py) |
| 07 GH C# component | 185 / 64 · 1 | 1,558 / 150 · 3 (2 py) | 1,559 / 1,419 · 3 (2 py) |
| 08 Python component with radio buttons (+ click) | 55 / 117 · 2 | 1,982 / 42 · 2 (2 py) | 1,982 / 62 · 2 (2 py) |
| 09 save a script | 62 / 68 · 2 | 134 / 8 · 1 (1 py) | 138 / 12 · 1 (1 py) |
| 10 run a script from a folder | 32 / 116 · 1 | 86 / 39 · 1 (1 py) | 89 / 40 · 1 (1 py) |
| 12 Karamba beam (state, dropdown) | 143 / 561 · 1 | 1,503 / 6,541 · 13 (1 py) | 1,236 / 25,782 · 11 (1 py) |
| 13 Marina Bay Sands script | 640 / 859 · 2 | 704 / 822 · 2 (1 py) | 707 / 706 · 2 (1 py) |
| 14 house → FE lines/shells → Karamba | 1,642 / 608 · 3 | 4,101 / 11,048 · 18 (3 py) | 3,791 / 42,631 · 17 (3 py) |
| 15 data tree via Python + wire | 214 / 99 · 2 | 290 / 366 · 4 (1 py) | 262 / 930 · 2 (1 py) |
| 16 messy canvas → regroup + tidy | 83 / 324 · 2 | 732 / 4,547 · 2 (1 py) | 537 / 13,286 · 3 (1 py) |
| 17 freeform canopy (+ inspect, raise crown) | 513 / 606 · 4 | 1,159 / 118 · 3 (3 py) | 1,168 / 240 · 3 (3 py) |
| 18 GH data trees (+ swap graft) | 166 / 221 · 2 | 1,725 / 5,587 · 10 (2 py) | 1,288 / 24,396 · 8 (2 py) |
| 19 1,224-object model: overview, find, count, image | 62 / 923 · 4 | 254 / 817 · 5 (3 py) | 190 / 1,212 · 4 (2 py) |
| 20 Karamba shell plate (+ thicker) | 280 / 264 · 2 | 2,305 / 9,515 · 16 (2 py) | 1,807 / 39,747 · 16 (1 py) |

Full tables, including every correctness check, are in [tables_v3.md](tables_v3.md). Every request and reply is in `out/<case>/<system>/`.

### Correctness: all 19 cases correct on all three systems

Every Grasshopper check also counts dangling wires (sources that are not real canvas objects): 0 on every system.

| check | value on every system |
|---|---|
| 01 frame | 36 columns, 51 beams, 3 slabs |
| 02 move → undo | top slab at x 12,000 → back at 9,000 |
| 03 / 04 tower | 1 capped loft, then 12 circles instead of rectangles |
| 05 fins | 24 fins baked on layer Fins |
| 06 Python component | 8 points + curve after the fix |
| 07 C# component | 8 towers |
| 08 radio click | Steel → Timber |
| 10 frame from a script file | 24 / 34 / 2 |
| 12 Karamba beam | **47.04 cm**, equal to the hand calculation PL³/48EI |
| 14 house FE | mass **99.6 t**, deflection 0.097 cm |
| 15 data tree | Mass Addition gives 7 results |
| 16 messy canvas | 6 groups, 0 overlaps, 72 fins intact |
| 17 canopy | crown raised to 8,650 mm |
| 18 graft swap | X grafted, Y plain; curve branches [6, 10] |
| 19 big model | longest beam BX24_L4 = 12,000 mm; 60 MEP runs in the region |
| 20 Karamba shell | **2.90 cm**, against plate theory 2.84 cm; 1.29 cm at 30 cm thick |

## 2. Method

**Same as v2**
- **Output** = what the model writes (tool arguments). **Input** = what it reads (results, images at w×h/750).
- **Fixed context** = tool schemas + instructions (+ the skill for Nextra).
- **Billed input** assumes each request re-sends the conversation.

**Same work on every system**
- Every case runs on all three systems, one after another, from the same starting state.
- Grasshopper definitions are written once, neutrally ([specs3.py](specs3.py)), and emitted in each system's own format: shorthand, McNeel graph JSON, RhinoMCP graph JSON.

**Python fallback**
- Where a system has no native tool for a step, the model uses that system's own Python tool, as a capable model would.
- This covers: component state (dropdowns, on-component buttons), right-click menus, Rhino references, script components, bake, flatten/graft flags, expressions, groups, layout, and the FE script's selection/oriented-box helpers.
- That code ([pyhelpers.py](pyhelpers.py)) is counted in every call that carries it.

**Recovery is realistic, never punitive**
- **Counted:**
  - Name lookups the model must make: ambiguous names, and plug-in names whose real name carries a suffix.
  - McNeel's rejection of ambiguous names and the re-placing that follows.
  - The C# type-hint fix that Rhino 8 forces after code is set on untyped inputs.
- **Not counted:** my own format guesses that turned out wrong (for example RhinoMCP's group colour format). The working form was used from the start.

## 3. What makes the difference

1. **Native coverage.** Nextra needed **0 Python fallbacks** in 43 calls. McNeel needed 34 of 108 and RhinoMCP 32 of 96, because neither has native tools for script components, component settings, menus, references, bake or flags. Each fallback carries 100–700 tokens of SDK code that must be exactly right.
2. **Grasshopper as shorthand.** A 21–27-component definition is 140–300 tokens of shorthand, against 900–1,500 tokens of JSON plus fix-up scripts.
3. **Replies that already contain the result.**
   - **Nextra:** about 60–600 tokens per build, results included.
   - **McNeel:** its build replies carry no data, so each build needs a canvas read-back of 2.8–6k tokens.
   - **RhinoMCP:** its replies always contain everything, about 6–13k tokens per build or read.
4. **Karamba and plug-in settings.** A Karamba model costs Nextra one call; the others need 11–18. Nextra writes on-component buttons, the unit dropdown and the cross-section menu inline (`$button[0]=true`, `$unitname="Point"`, `menu="Units>Shell - Const"`). The others need an archive-editing script.

   Re-reading the archive turns the component's wires into unresolved proxies, so the script must relink them. The first attempt here lost the Support's inputs, and the model reported "no supports". A second attempt re-added the wires but left the proxies behind, which showed as wires drawn from the canvas origin.
5. **Helpers in `run`.** `q('layer:X')`, `obb()` and the script library keep Rhino edits to 15–50 output tokens (case 02: 48 against about 645). The FE conversion in case 14 needs substitute helpers on the other systems.

**Where the systems are even**
- **Plain one-shot Rhino scripts** (01, 13): output is the same script everywhere. Nextra reads a little more after a build, because its reply lists what was made: sizes, layers and handles.
- **Large-model questions answered by a short script** (19): the totals are close.

## 4. Things found and fixed in this round

**In Nextra**
- **Component search:** plug-in names with a suffix now count as exact matches, and weak fuzzy matches are dropped. A 6-name lookup fell from about 830 to 300 tokens.
- **Phantom checkpoint id:** a *failed* `run` that changed nothing no longer prints a checkpoint id.
- **Wires drawn from (0,0):** setting `$state` on a component that is *already wired* re-reads its archive, which turns its input wires into "Unresolved Proxy" sources with no position. GH then draws them from the canvas origin. `$state` now relinks proxy sources to the real objects and drops any left over. Regression c27 checks this, and every v3 GH check counts dangling wires.

**In the tests**
- The regression case for the house FE model only checked the mass. A 0.5 m mesh exceeds Karamba's 50-shell trial limit, so the analysis never ran without the test noticing. It now uses a 3.5 m mesh and requires a real deflection.

**About the other systems** (behaviours, not errors)
- **McNeel:**
  - Needs the exact installed name, including Karamba's leading space (`" Analyse (Karamba3D)"`).
  - Its Python runs on CPython, where `GH_DataMapping.None` is not a valid attribute.
- **RhinoMCP:**
  - Its Python runs on IronPython 2, which needs `clr.AddReference('GH_IO')` and typed .NET arrays.
  - `gh_mutate_graph` accepted a groups list but created none.
  - `gh_layout_components` leaves the canvas unsolved until the next Grasshopper event. The neutral check solves once before counting.
- **Both:** the C# component's code setter sits on a base class, and the legacy C# component (hidden, obsolete) has none.

## 5. Caveats

- **One run.** Tokens are estimated as chars/4 for all systems; the model's own reasoning text is not counted.
- **I wrote the competitors' paths and Python.** A model might find shorter paths, or need several attempts where this harness used the known-good form. Their real cost is therefore more likely higher than shown, not lower.
- **The Karamba trial licence (50 shells) shaped the shell cases.**
