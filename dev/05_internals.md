# 05 — Internals

## 1. Handles (`Core/Handles.cs`)

- Two tables in the state bag: Rhino `r` (GUID ↔ number) and GH `g` (GUID ↔ number), each keyed by document. The Rhino doc serial number or the GH `DocumentID` is the key, so a new or different document starts a new table.
- A handle is assigned the first time an object is mentioned in a reply, and stays the same for the session (including across engine reloads). New objects get new numbers; numbers are never reused.
- GH groups also get `g` handles.
- `Fmt`: compact number formatting (`N`: ≥1000 → 1 decimal, ≥1 → 3, <1 → 4, no trailing zeros), points `(x,y,z)`, dims `LxWxH`, `Trunc`.
- `Fmt.FilterTokens`: splits filters while allowing values with spaces (`type:Bounding Box`, `layer:My Layer`).
- `ViewTool.Ranges` compresses handle lists: `r1 r2 r3 r7` → `r1-r3 r7`.

## 2. Rhino deltas (`Rh/DeltaTracker.cs`)

- Created at the start of `run`, `gh_edit {bake}` and undo. It subscribes to `RhinoDoc` add / replace / delete / modify-attributes events.
- For modified and deleted objects it keeps a **copy of the "before" geometry and attributes** on first touch. That is what makes undo exact without touching the user's own undo stack.
- `Report(reply, max)`:
  - ≤ 12 changes: one line each, using `Geo.Describe`.
  - More: one line per (change, layer, type, shape class, rounded size) with handle ranges, e.g. `+36 polysurface solid [Columns] 3200x400x400 linear vertical r1-r36`.
- `Revert(doc, added, before)`: deletes the added objects and restores the before-copies (replace, or re-add with the same id).

## 3. GH delta (`Gh/GhCore.cs`)

When `run` code touches GH:
1. Before the code runs, a signature per GH object is taken (type, nickname, sources, persistent data, position).
2. After it runs, the signatures are compared.
3. The changed objects are written as shorthand (`ReportDelta`).

So even raw Python edits come back to the LLM in the shorthand it already reads.

## 4. Checkpoints and undo (`Core/Checkpoints.cs`)

- `Begin(label, withGh)` runs before every mutating call. It creates `cN` and, if a GH document exists, stores `GH_Archive` bytes of the whole document. The last 30 checkpoints are kept.
- `Complete(c, delta)` attaches the Rhino delta: added ids plus before-copies.
- `Discard(c)` drops a checkpoint when nothing changed.
- `RevertTo(id, steps)` reverts every checkpoint from `id` to now, **newest first**:
  - Rhino part: `DeltaTracker.Revert` per checkpoint.
  - GH part: the **oldest** archive per GH document among the reverted checkpoints is restored (`GhCore.Restore`), because that archive is the state before all of them.

## 5. Geometry understanding (`Rh/Geo.cs`)

- **Oriented box** (`ComputeObb`): the minimum-volume box over a few cheap candidate frames.
  - Candidates: the world frame; the dominant planar face with x along its longest edge; and the PCA frame of the vertices (covariance plus Jacobi eigen-decomposition, in `Pca`).
  - No mass properties are computed, so it stays fast on hundreds of objects.
- **Shape class** from the sorted box dimensions L ≥ W ≥ H:

  | class | condition | axis given |
  |---|---|---|
  | `linear` | L ≫ W | the long axis (`vertical`, `x`, or a vector) |
  | `planar` | H ≪ W | the normal (`n+z`) |
  | `block` | otherwise | — |

- `Describe` is the one-line form used everywhere: handle, type word, `solid`/`open`, layer, name, box, shape, centre.
- `Detail`: topology counts, faces **grouped by normal direction** with areas (a cheap description of a solid's shape), user text, length/area/volume when cheap.
- `Verts`: capped vertex / control-point lists, the last level of the ladder.
- `Selector.cs`: parses the filter string into a predicate. Handles OR together; all other tokens AND together. `cells:` uses the last Rhino image's projection (`ViewTool.InCells`).

## 6. GH layout (`Gh/Layout.cs`)

Two-level layered (Sugiyama-style) layout, ported from the old nextra.gh and changed in several places:
- **Micro level:** the members inside each group, using real port grip positions.
- **Macro level:** groups and loose objects as blocks.

`Layer(items, edges, …)`:
1. **Ranks** = longest path to a sink. Sinks are rank 0 and drawn rightmost, so data flows left → right.
2. **Pass A** (right → left): each item's y = the barycentre of *all* its downstream grips, not just the first wire.
3. **Pass B:** sinks follow their sources.
4. **Resolve overlaps** in each column.
5. **Loose members** (no wires inside the block, e.g. a group of sliders): packed tightly and **ordered by where their consumers sit**, using a snapshot taken before anything moves. Wires then leave the block uncrossed.
6. Scribbles are stacked above the column of their nearest item.

Two fixes for plug-in components (Karamba's collapsible inputs):
- `SetPivot`: moves the child param attributes by the same delta first. Some plug-ins do not re-place hidden params in `PerformLayout`, and stale param rects stretch the component's bounds across the canvas.
- `RepairBounds`: snaps each param back to the offset it has on a fresh instance. This removes stretched bounds and stray grips floating above the component.

Entry points:
- `Place` (after edits): only new nodes move; joined groups re-lay out in place; `PushAway` cascades overlaps downward.
- `Tidy`: whole canvas or a subset, groups kept as blocks.
- `AddRelays` (`relays:true`, idea from `dev/reference/shiba_tidy`):
  - Every wire crossing a group boundary gets a typed relay param: `<name>_In` at the left of the receiving group, `<name>_Out` at the right of the sending group.
  - A relay's type and name come from the **source** param.
  - Relays are tagged with `Description = "nextra-relay"`, so re-running adds none.

## 7. Component state, menus, clicks

**`StateIO.cs` — `$key=value`**
- The component is serialized to a GH archive chunk and flattened to keys such as `Active/button[2]`, `SelectedIndex` or `Family`.
- Compared against a **fresh instance** of the same component, so only non-default keys are shown.
- Keys can be shortened to any unique suffix (`button[2]`).
- On write, the old item is removed, the new value is set and the archive is read back into the object. The remove matters because GH_IO setters append; skipping it once caused duplicate-item crashes.
- This covers anything a component saves in a `.gh`: dropdowns, modes, Karamba on-component check-boxes, right-click toggles.

**`MenuIO.cs`**
- `Describe`: asks the component to build its own context menu (`AppendMenuItems`) and lists the component-specific items with their check state, e.g. `[x] Union Box | Mode>Linear`.
- `Click`: clicks an item by name. `:on` / `:off` clicks only when needed; `A>B` walks a sub-menu. Items ending in "…" (they open dialogs) are refused.
- `ClickAt`: sends a mouse down/up to the component's attributes at a fractional position. It is the last resort for custom widgets.

## 8. Script components (`Gh/Scripts.cs`) and `run` (`Core/RunTool.cs`)

**Script components** (Rhino 8 Python 3 / C# / IronPython, plus legacy GhPython / C# Script):
- Code goes through the component's own source setters, with no editor UI.
- Python ports come from shorthand declarations, with access (`item`/`list`/`tree`) and type hints (`TypeHints.Select`).
- C#: `ParseRunScript` reads the `RunScript(...)` signature and the params are set **before** the source. Rhino 8 regenerates the signature from the params, so the order matters.
- `mirror:true` writes `.nextra/scripts/gN.py` with line numbers for long edits; `from_mirror:true` reads it back.

**`run`:**
- Uses the RhinoCode API: finds the language, loading the RhinoCode plug-in silently if needed, and runs with captured stdout/stderr.
- The helpers (`h`, `q`, `obb`, `ghdoc`, `args`) are injected through a prelude. Data passes through AppDomain data.
- `Patch.Apply`: exact find/replace with a whitespace-insensitive retry. On failure it returns the closest text (Levenshtein).
- **Error text:**
  - .NET frames are stripped (`PyOnly`) and duplicate tracebacks collapsed.
  - The failing line is shown with its neighbours (`Context`).
  - A normalised signature (numbers → N, names → …) is logged.

## 9. Memory (`Core/Memory.cs`)

In `%APPDATA%/Nextra/memory/` (or config `memory_dir`):

| file | cap | content |
|---|---|---|
| `prefs.md` | 1500 chars | user preferences, `key: value` lines replace by key |
| `rules.md` | 2000 chars | learned rules. Changed **only** via `compact` → user approves → `set_rules` |
| `mistakes.log` | none | JSON lines, auto-logged on every tool error plus manual `log_mistake` |
| `library/` | — | saved scripts with a docstring (the first line is the index summary) |
| `plugins/` | — | user plug-in guides, which override the bundled ones |

- `RecurringHint`: when the error just logged has occurred 3+ times and no rule mentions it, the reply says "recurring mistake…" once, prompting the LLM to log and later compact.
- `Compact`: groups the log by signature with counts and the latest fix. The LLM turns that into rule proposals for the user.
- **Library** (`Library`): the memory library + config `library_dirs` + `<3dm folder>/.nextra/library` + `<3dm folder>/scripts`. Only an index (name + first docstring line + args) goes to the LLM; `read_script` / `query {scope:'library', name}` returns the code.
- **DocsStore:** serves `skill/nextra/*.md` (and user plug-in guides) through `query {scope:'docs', filter}`, by file name, section or keyword.

## 10. Plug-in guides (`Gh/PluginDocs.cs`)

- Sources: `memory/plugins/*.md` (user, checked first, wins) then `<skill_dir>/plugins/*.md`. Files starting with `_` are ignored.
- The `plugin:` front-matter line lists the GH categories it covers; without it, the file name is the category.
- `Check(categories, reply)` is called by `gh_edit` (categories of touched nodes and `@Category` names in the ops) and by GH `query`. A guide appears **once per session**:

  | mode | output |
  |---|---|
  | `auto` | its `## Rules` section |
  | `hint` | a pointer line |
  | `off` | nothing |

  Mode comes from config `plugin_docs` or the per-call `plugin_docs` argument.

## 11. Dialog handling (`Nextra.Host/Dispatcher.cs`, `DialogProbe`)

Finds blocking popups in two ways:
- the last active popup of each disabled top-level window of the Rhino process, and
- any other visible, titled top-level window that is not Rhino's main window or the GH editor. Plug-in message boxes are often like this.

What it does with them:
- Reads the title and the child control text.
- **Never sends keystrokes.** Esc once interrupted a running GH solution and triggered a breakpoint.
- `Dismiss` posts IDCANCEL / WM_CLOSE to modal dialogs only.
- `AutoDismiss` closes only titles listed in config `auto_dismiss` (default: Karamba's EPPlus notice).
- Limitation: for dialogs a plug-in draws itself, the title is reported but often not the body text.
