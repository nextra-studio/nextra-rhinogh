---
description: Grasshopper × Python — script component bodies, driving the canvas from run, custom component UI (Rhino 8)
---
# GH × Python

## (A) Inside a Python 3 script component
| task | code |
|---|---|
| inputs / outputs | input names are variables; assign outputs by name (`a = crv`) |
| list / tree access | declare in shorthand: `(pts:list=g3, t:tree=g4)`; type hint `pts:list:Point3d` |
| tree out | `from Grasshopper import DataTree; from Grasshopper.Kernel.Data import GH_Path; t = DataTree[object](); t.Add(v, GH_Path(0, i))` |
| call components | `import ghpythonlib.components as ghc; ghc.Voronoi(pts)` |
| self | `ghenv.Component` (`.Message = "ok"`, `.AddRuntimeMessage(Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning, "msg")`) |
| persist between runs | `import scriptcontext as sc; sc.sticky["key"]` |
Errors come back as `Error running script: … [line:col]` with the failing lines; fix with `gh_edit {script:{handle, patch:[…]}}`.

## (B) From `run` (code outside the canvas) — fallback when shorthand cannot express it
```python
d = ghdoc()                              # active GH document (created if missing)
c = h('g12')                             # GH object by handle
for o in d.Objects: print(handle(o), o.Name)
p = c.Params.Input[0]; p.Sources        # wiring
src = h('g3').Params.Output[0]; c.Params.Input[1].AddSource(src)
c.ExpireSolution(False); d.NewSolution(False)
```
`run` reports the GH delta in shorthand automatically. Prefer shorthand; use this for: reading data trees in detail, bulk edits, things no component setting covers.

## Custom UI on a script component (Karamba-style buttons) — Rhino 8, verified
Rules learned the hard way:
1. Subclass `GH_ComponentAttributes` **and implement `RhinoCodePlatform.GH.Context.IScriptAttribute`** (one method `ShowProgress(self, p, m): return False`). Without it the component fails on the next rebuild ("Unable to cast … IScriptAttribute").
2. pythonnet needs a unique `__namespace__` per class version → derive it from a hash of the script source and cache the class in `sc.sticky[key]`; otherwise edits to `Render` are silently ignored (old class reused).
3. Do not subclass the component's own attribute class (sealed / "Access is denied").
4. Store the UI state somewhere persistent (`ghenv.Component.Message`, a value list, or the archive); click → set state → `ExpireSolution(True)`.
5. If a broken attribute class is stuck on a component: `run` → `c.CreateAttributes()` then re-solve.
Working example: `test/v1/08_radio_buttons/radio_component.py` (radio rows under the component, choice persisted in Message).

## Pitfalls
- `Rotate`, `Rectangle`, `Line`, `Circle` exist in several categories — give the ports you use; the engine picks the matching one.
- `Range` returns N+1 values (use `N=n^e(x-1)` for N items).
- Script component inputs default to item access; lists need `:list`.
- Python 3 vs IronPython 2: syntax and libraries differ (`super()` with no args only in Py3).
