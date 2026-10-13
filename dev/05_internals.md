# 05 — Internals

## 1. Handles (`Core/Handles.cs`)

- Tables in the state bag: Rhino `r` (GUID ↔ number) and GH `g` (GUID ↔ number), **one table per document** (key `handles.<prefix>.<doc>`: Rhino doc serial number or GH `DocumentID`). Switching between GH documents and back keeps every handle meaning the same object; an old `g12` can never silently hit a node of another document.
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
  - Rhino part: restore original layers/current layer, `DeltaTracker.Revert` objects per checkpoint, then remove layers added by that call. Layer snapshots are serialized JSON and CRCs; layer-only edits also retain checkpoints.
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

In `%APPDATA%/NextraRhinoGh/memory/` (or config `memory_dir`):

| file | cap | content |
|---|---|---|
| `rules.md` | 2000 chars | learned rules. Changed **only** via `compact` → user approves → `set_rules` |
| `mistakes.log` | none | JSON lines, auto-logged on every tool error plus manual `log_mistake` |
| `library/` | — | saved scripts with a docstring (the first line is the index summary) |
| `plugins/` | — | legacy plug-in guides, still read after the new user folder |

- `RecurringHint`: when the error just logged has occurred 3+ times and no rule mentions it, the reply says "recurring mistake…" once, prompting the LLM to log and later compact.
- `Compact`: groups the log by signature with counts and the latest fix. The LLM turns that into rule proposals for the user.
- **Library** (`Library`): the memory library + config `library_dirs` + `<3dm folder>/.nextra/library` + `<3dm folder>/scripts`. Only an index (name + first docstring line + args) goes to the LLM; `read_script` / `query {scope:'library', name}` returns the code.
- **DocsStore:** serves `skill/nextra/*.md` (and user plug-in guides) through `query {scope:'docs', filter}`, by file name, section or keyword.

## 10. Plug-in guides (`Gh/PluginDocs.cs`)

- Sources: `user/plugins/*.md` first, then legacy `memory/plugins/*.md`, then `<skill_dir>/plugins/*.md`. Files starting with `_` are ignored.
- The `plugin:` front-matter line lists the GH categories it covers; without it, the file name is the category.
- `Check(categories, reply)` is called by `gh_edit` (categories of touched nodes and `@Category` names in the ops) and by GH `query`. A guide appears **once per session**:

  | mode | output |
  |---|---|
  | `auto` | its `## Rules` section |
  | `hint` | a pointer line |
  | `off` | nothing |

  Mode comes from config `plugin_docs` or the per-call `plugin_docs` argument.

## 11. Dialog handling (`Nextra.Host/DialogProbe.cs`)

Rule: the probe runs on the server thread and must **never wait for the Rhino UI thread** (it may be busy for minutes — that is exactly when status and timeouts are needed):
- the main window handle is cached at start (`RhinoApp.MainWindowHandle()` marshals to the UI thread);
- titles are read with `InternalGetWindowText` — `GetWindowText` on a window of our own process *sends* WM_GETTEXT and blocks until the UI thread answers (this once made timeouts and `status` hang during long scripts);
- WPF/Eto dialogs (e.g. `Rhino.UI.Dialogs.ShowMessage`) have no child HWNDs: their text and button names come from UI Automation on a helper thread with a 1.5 s limit (a modal loop pumps messages, so it answers);
- `Dismiss` posts IDCANCEL/WM_CLOSE and then **verifies** the window is gone; one that stays is reported ("ask the user to answer it in Rhino").

Finds blocking popups in two ways:
- the last active popup of each disabled top-level window of the Rhino process, and
- any other visible, titled top-level window that is not Rhino's main window or the GH editor. Plug-in message boxes are often like this.

What it does with them:
- Reads the title and the child control text (or UI Automation text for WPF/Eto).
- **Never sends keystrokes.** Esc once interrupted a running GH solution and triggered a breakpoint.
- `AutoDismiss` closes only titles listed in config `auto_dismiss` (default: Karamba's EPPlus notice).
- Limitation: a dialog a plug-in paints entirely itself (no HWND children, no automation tree) shows only its title.

## 12. API reference (`Core/ApiDocs.cs`)

- **Sources** (always the installed versions):
  - RhinoCommon and Grasshopper / GH_IO by reflection on the loaded assemblies — real signatures, return types, static/instance, obsolete flags, enum values — joined with the XML docs Rhino ships next to them (`RhinoCommon.xml`, `Grasshopper.xml`, `GH_IO.XML`) by documentation id;
  - rhinoscriptsyntax from its Python sources (`~/.rhinocode/py39-rh8/site-rhinopython/rhinoscript/*.py`, docstrings split into description / returns / parameters / example).
- **Cleaning:** `<see cref>` → short names, paragraphs kept, wrapped lines joined, short type names (`IEnumerable<Curve>`, `int?`), operators as `operator +`, property accessors and `EditorBrowsable(Never)` dropped. Obsolete members stay in the index but rank last and are labelled.
- **Cache:** `%APPDATA%/NextraRhinoGh/api/index.json` + `stamp.txt` (Rhino version + GH version + format number; bump `Format` when the cleaning changes). First query builds it (~2 s, ~31k items). Tidy per-namespace Markdown in `api/md/<rc|gh|rs>/<Namespace>.md` for humans.
- **Search:** every query word must match somewhere (name, camel-case word of the name, type, or summary); name matches weigh most; everyday types (Brep, Curve, Mesh, Point3d, RhinoDoc, GH_Document…) and namespaces (Rhino.Geometry, Grasshopper.Kernel) rank up, interop/UI namespaces down. Overloads collapse into one line (`+2 overloads`). Detail output prints each doc line once across overloads.

## 13. The user's folder (`Core/UserDocs.cs`)

- Default `%APPDATA%/NextraRhinoGh/user` (config `user_dir`), created at Rhino start through `memory {action:'init'}` from the host, seeded with README.md, preferences.md and `_TEMPLATE.md` files — **never overwritten**. Outside the Yak package folder, so plug-in updates do not touch it. The host copies this plug-in's old `%APPDATA%/Nextra` settings and user files once without removing them.
- AI `save_pref` and `set_prefs` write here too, to `preferences.md` (1500 active-character cap); old `memory/prefs.md` entries are imported once. `memory list` reports the folder path and a compact view.
- `memory list` shows preferences (comment lines stripped, capped), the workflow index (file name + `when:` line) and the list of the user's plug-in guides. `query scope:'docs'` reads `preferences`, `workflows/<name>`, `plugins/<name>` (marked "yours").
- Plug-in guides: user folder first, then the old `memory/plugins`, then the bundled `skill/plugins`; the first guide claiming a category wins. Front matter is read by `UserDocs.Meta` (`plugin:`, `when:`, `description:`).
- `save_workflow` requires a `when` line; `save_guide` adds front matter and a `## Rules` stub if missing; both keep a `.bak` of the previous version.
- Why this design: a skill uploaded to claude.ai is a fixed bundle the user cannot extend. User-specific instructions therefore live on the user's machine and reach the model through the tools — the same for every client.
