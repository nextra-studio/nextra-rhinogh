# 03 — The six tools

The schemas the LLM sees are in `bridge/server/index.js` (`TOOLS`). They are kept short, because the schema is paid for on every request (≈1.3k tokens in total). Longer explanations live in the skill.

Every reply: `{ok, text, images[]}`. Unknown argument names are reported as `(ignored args: …)`.

---

## view — look

Image of Rhino (`target:'rhino'`), the GH canvas (`gh`) or the unbaked GH geometry shown in Rhino (`gh_preview`), with an 8×8 grid and handle tags.

| arg | meaning |
|---|---|
| `target` | `rhino` (default) · `gh` · `gh_preview` |
| `focus` | cells of the **last image** `C3:E5` · handles `r3 r7` / `g12` · filter `layer:Walls` / `group:Inputs` |
| `view` | `current` `top` `front` `right` `left` `back` `persp` `iso` or a named view |
| `mode` | display mode, default shaded (`current` keeps the user's) |
| `tags` | `auto` · `on` · `off` |
| `isolate` | hide everything except the focus |
| `size` | `[w,h]`, default 800×600 |

**How it works**
- *Region-first.* The overview tags only if it is not crowded. A crowded view returns **per-cell object counts** and no tags; the LLM then picks cells and calls `view {focus:'C3:E5'}`. That re-renders the same camera, cropped from a supersampled capture, with tags.
- *Tags are text next to the element*, rotated along linear members and placed beside planar/blocky ones, with a white halo. They are not dots. A candidate-position search plus an overlap test keeps labels from colliding.
- *Occlusion.* An object hidden behind others gets a faded tag ending in `*` (the camera ray is cast against the other objects' meshes).
- The user's viewport is always restored. Capture uses `RhinoView.CaptureToBitmap` at the viewport's own aspect ratio, so the frustum is unchanged and image ↔ world mapping is exact.
- Lowercase `g1`–`g8` means a GH handle in `focus`; uppercase `G1`–`G8` means a grid cell. Cell ranges such as `C3:E5` are unambiguous. Frame/cell inclusion uses projected bounds, including long geometry whose centre is outside the crop.
- GH canvas: rendered by GH's own painter on a private viewport (`GH_Canvas.GenerateHiResImageTile`), so the user's canvas is untouched; a schematic drawing is the fallback. Errors (red components) are listed.

**Reply example**
```
rhino view 'Perspective' 800x600, zoom extents. Grid A-H x 1-8.
7 object(s) in frame, tagged: r1-r7
```
```
gh canvas 800x600, whole canvas, zoom 0.69 (gh). Grid A-H x 1-8. 14 object(s) visible.
tagged: g1-g12 g17 g18
errors (red): g11
```

---

## query — read (text only, cheapest first)

| `scope` | answers |
|---|---|
| `rhino` (default) | model contents |
| `gh` | the Grasshopper definition as shorthand |
| `components` | installed GH components with ports (replaces a RAG index) |
| `api` | cleaned API reference of the installed versions: RhinoCommon, Grasshopper SDK, rhinoscriptsyntax (see 05 §12) |
| `docs` | the skill documents + the user's workflows / plug-in guides / preferences (marked "yours") |
| `library` | saved scripts |
| `status` | busy / dialog state, works even when Rhino is blocked (`dismiss_dialog:true` cancels the modal) |
| `instances` | answered by the bridge: open Rhinos and which one this session is bound to |

`level`: `summary` → `list` → `detail` → `verts` (rhino) / `data` (gh). `filter` selects. `limit` / `offset` page.

**Rhino filter tokens** (all AND together): `r3 r4` · `layer:X` · `type:curve` · `name:C*` · `sel` · `hidden` · `cells:C3:E5` · `bbox:x0,y0,z0,x1,y1,z1` (touches) · `inside:…` (fully inside).
**GH filter tokens:** `g12` · `group:X` · `type:Loft` · `name:nick*` · `errors` · `warnings` · `sel` · `cells:…` · `near:g12:2` · `all`.
Names, layers, groups and types match **exactly** (case-insensitive) unless the pattern has `*`/`?` — `layer:Beams` never picks "Beams_old", because code acts on what it selects. `layer:FE` includes its sub-layers.

**API search** (`scope:'api'`, `filter`):
- free words → ranked one-liners: `Brep.CreateFromLoft(IEnumerable<Curve> curves, …) → Brep[] · static · +2 overloads — summary`
- `Type.Member` → full docs of all overloads (params, returns, remarks, example; Python note for `out` parameters)
- `Type` → overview: summary, ctors, static / props / methods as names with overload counts
- `rs.Name` → rhinoscriptsyntax; `src:gh` / `src:rc` / `src:rs` restricts the source; misspellings get "did you mean"
**components filter:** a name (`extrude`), several (`loft, divide curve`), or a category (`@Karamba3D`).

**Rhino levels, with real replies**

`summary`: units, layers × types, extents.
```
doc 'untitled' units millimeters tol 0.001; 0 objects
next: query filter:'layer:X' (list) → level:'detail' → level:'verts'; or view.
```
`list`: one line per object, **with an oriented box**.
```
r72 polysurface solid [Slabs] "S_L1" 18600x12600x250 planar n+z @(9000,6000,13875)
```
Read it as: handle, type, layer, name, oriented box L×W×H, shape class (`linear` / `planar` / `block`) with axis, and centre.
- `3200x400x400 linear vertical` is a column.
- `13000x7000x250 planar n+z` is a slab.

`detail`: topology, faces grouped by normal, areas, user text. `verts`: vertices / control points, capped. This level is never reached unless the LLM asks for it.

**GH `query`** (see 04_shorthand for the grammar):
```
## Twist #FFD6AB (g15)
g11 = Loft (C=g18.G^f)
  ! Loft could not be constructed.
  => L:  {0}
```
`=>` lines are output data summaries, `!` lines are runtime messages. `detail` adds the ports (`inputs:` / `outputs:`), `$` state keys, the right-click menu and code. `data` lists the values per tree path.

---

## run — execute code in Rhino

| arg | meaning |
|---|---|
| `lang` | `py` (Python 3, default) · `cs` |
| `code` | source |
| `patch` | `[{find, replace}]` applied to the previous code of this language. Each `find` must match exactly once; failures explain why and suggest the closest text |
| `file` | run a file |
| `lib` + `args` | run a library script by name; `args` is a dict available as `args` |
| `save_as` | on success, save to the library |
| `label` | name for the checkpoint |

**Helpers available in the code** (prelude): `doc`, `rg` (Rhino.Geometry), `h('r3'|'g5')` → object, `handle(obj)`, `q('layer:X bbox:…')` → objects (same filter language as `query`), `obb(obj)` → `{shape, L, W, H, axis, center, box}`, `ghdoc()`, `args`. Grasshopper is auto-imported.

**What a reply contains:** stdout, the Rhino delta, the GH delta (only if the code touched GH), errors, checkpoint id.
```
ok py 3921ms, checkpoint c2
stdout:
built 7
rhino delta +7:
+ r1 polysurface solid [Columns] 3200x400x400 linear vertical @(0,0,1600)
…
```
- With more than 12 changes the delta is **aggregated** into one line per (change, layer, type, shape, size) with handle ranges (`r1-r36`).
- Delta marks: `+` added, `~` modified, `-` deleted.
- On error, the failing line plus neighbours, and a one-line signature. The LLM fixes it with `patch`. The error is also auto-logged to `mistakes.log`.

---

## gh_edit — change the Grasshopper canvas

| arg | meaning |
|---|---|
| `ops` | shorthand lines |
| `script` | `{handle, code \| patch:[…] \| file \| mirror:true \| from_mirror:true}` edits one script component |
| `layout` | `auto` (default) · `tidy` · `tidy:<filter>` · `none`; plus `relays:true` for group interface params |
| `open`, `new`, `save` | document actions (no GH dialogs; save uses an archive write) |
| `bake` + `layer` | bake components' outputs to Rhino; returns new `r` handles |
| `solve`, `atomic`, `echo`, `show` | solve control; `atomic` = all or nothing; `echo` prints the resulting shorthand; `show` opens the editor |
| `plugin_docs` | `auto` / `hint` / `off` for this call |

**Flow** (`GhEditTool.Run`): checkpoint (GH archive) → parse → `Builder.Apply` (independent lines) → layout → one solve → report.

**Reply example**
```
ok: 14/14 lines applied (+14 nodes, 16 wires, 2 values, +4 groups), checkpoint c1
ids: n=g1 fh=g2 w=g3 tw=g4 ser=g5 …
groups: Inputs=g15 Floors=g16 Twist=g17 Massing=g18
note: L10: 'Rectangle' is ambiguous; used Curve/Primitive because its ports match P,X,Y
solved 12ms, no errors/warnings on affected nodes
results: g14.B: 1 Brep {0}
```
- Status line: `ok`, `partial` or `FAILED`.
- Failed lines: `L7 \`…\`: reason — valid alternatives`. Only these need fixing; the rest stays applied.
- After the solve: runtime errors of touched nodes and anything newly broken downstream. "Input parameter failed to collect data" is collapsed as a consequence, not a cause.
- `results:` summarises what the sinks produced (count, type, tree path).
- The first time a plug-in's components are used, its `## Rules` are appended once.

---

## undo — revert

`undo {list:true}` · `undo {checkpoint:'c4'}` · `undo {steps:2}`.
Reverts from that checkpoint to now, newest first. See 05 §4.
Includes Rhino objects and layer/current-layer changes plus GH document archives. Script effects on external files, arbitrary document settings, materials, and other tables are outside the checkpoint contract.

```
reverted:
c4: gh: document restored (5 objects)
```

---

## memory — what the system keeps

| `action` | effect |
|---|---|
| `list` | prefs, learned rules, library index, recurring mistakes. Call at session start |
| `save_pref {text}` / `set_prefs` | add / replace compact `user/preferences.md` entries (`key: value` replaces an existing key) |
| `log_mistake {text, fix}` | manual lesson (errors are also auto-logged) |
| `compact` | groups the mistake log by signature, for the LLM to propose rules |
| `set_rules {text}` | write the rules file, **only after the user approves** |
| `save_script {name, code, doc, lang}` / `read_script` / `delete_script` | script library |
| `save_workflow {name, when, text}` / `save_guide {name = plug-in category, text}` | save verified reusable workflows and user-specific plug-in rules in the persistent user folder; the model reads full files on demand |
| `init` | creates the user folder (the host calls it at Rhino start; `NextraFolder` opens it) |

Details in 05 §9.
