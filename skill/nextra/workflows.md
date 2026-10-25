---
description: Recipes — Rhino build/modify, GH build/modify/tidy, Rhino↔GH, scripts & library, structural (Karamba), recovery
---
# Workflows (each step = one tool call; keep replies small)

## Rhino: build from scratch
1. `memory list` (units/layer prefs) → `query` (summary: units, layers).
2. `run` one script that creates everything with layers + names (`attrs.Name="C01_L1"`) → read the delta (aggregated by layer/type/size).
3. `view {view:'iso'}` once. Crowded → `focus:'C3:E5'` or `focus:'layer:Beams'`.
4. Worth reusing? `run {…, save_as:'frame_building'}` (add a one-line docstring + `args`).

## Rhino: modify ("add a storey", "move this part")
1. Find targets with text, not images: `query {filter:'layer:Slabs'}` / `level:'detail'`.
2. Library first: `query {scope:'library'}` → `run {lib:'frame_building', args:{storeys:1, z0:10500}}`.
3. Select in code with the same filter syntax: `for o in q('layer:Columns inside:x0,y0,z0,x1,y1,z1'): …` or handles `h('r12')`. Names/layers match exactly (use `*` for patterns: `name:*_L4`; `layer:FE` includes its sub-layers); `bbox:` = touches the box, `inside:` = fully inside.
4. Check the delta (`~` modified, `+` added). Wrong? `undo {checkpoint:'cN'}`.

## Geometry understanding (cheap → expensive)
summary → list (oriented box `LxWxH linear|planar|block`, axis) → detail (faces by normal, areas) → verts. Members: linear vertical = column, linear horizontal = beam, planar n±z = slab, planar horizontal normal = wall. Centre lines = the long axis of the oriented box (`detail` gives it); for complex solids slice or use `run`.

## GH: build from scratch
1. Read `shorthand.md` once per session. Unsure of a name/ports → `query {scope:'components', filter:'loft, divide curve'}`.
2. One `gh_edit {ops}` with `##` groups (inputs → logic → geometry → output). Use sliders for every number the user may tune.
3. Read the reply: fix only failed lines (handles given), check `results:` counts/types. Then `view {target:'gh'}` and, for geometry, `view {target:'gh_preview'}`.

## GH: modify one part
`query {scope:'gh', filter:'group:Twist'}` → small ops: `g12 (A=g20)`, `- g8`, `## g16\nnew = …` (joins that group, which re-lays out in place). Never rebuild what works.

## GH: messy definition → tidy
1. `query {scope:'gh'}` (groups, ungrouped, errors) + `view {target:'gh'}`.
2. Decide stages; regroup with ops: `## Inputs #DBABFF` + bare handles, `## g40 "Profile"`.
3. `gh_edit {layout:'tidy'}` (or `{layout:'tidy', relays:true}` for block-style groups with _In/_Out params) → `view` to confirm. Delete dead ends (`- gN`) only when the user agrees.

## Rhino ↔ GH
- Rhino → GH: `crv = Curve {ref=[r3,r4]}` or `(C=r3)` on an input.
- GH → Rhino: `gh_edit {bake:['g27'], layer:'Fins'}` (returns new r-handles).
- Unbaked result check: `view {target:'gh_preview', focus:'g27'}`.

## Scripts
- Rhino: `run` (py default). GH script component: create with shorthand, edit with `gh_edit {script:{handle, patch}}`; long code: `script:{handle, mirror:true}` writes `.nextra/scripts/<file>_gN.py` (path in the reply), edit lines, then `from_mirror:true`.
- C# GH: put the `RunScript(...)` signature in code; ports follow it.
- Rhino commands (export/import, Make2D, plug-in commands without an API): `run` → `Rhino.RhinoApp.RunScript("_-Export \"C:/out.dwg\" _Enter", False)`. Always the dash form with every option inline; a dialog or open prompt blocks the bridge. Prefer RhinoCommon when an API exists.

## Structural (FE)
Rhino solids → `query level:'detail'` to classify members → `run` to make centre lines (columns/beams) and mid-surfaces (slabs/walls) on layer `FE` → GH: `ref` those curves → Karamba (`plugins/Karamba3D.md` auto-loads) → check `Mass`, `Info`, `MaxDisp` → `view gh_preview`.

## Recovery
- `timeout` / `busy`: wait, `query {scope:'status'}`; a dialog is named with its text → tell the user; `dismiss_dialog:true` only if it is safe.
- Same error twice: switch approach (component → script, or script → component). Log it: `memory log_mistake`.
- Everything wrong: `undo {list:true}` → `undo {checkpoint:'cN'}`.
