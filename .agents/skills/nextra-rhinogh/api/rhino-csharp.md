---
description: Rhino 8 × C# (RhinoCommon) for `run {lang:'cs'}`
---
# Rhino × C# (run lang:'cs')

Script = top-level statements (Roslyn). Prelude: usings for System, Linq, Collections.Generic, Rhino, Rhino.Geometry, Rhino.DocObjects; `var doc = RhinoDoc.ActiveDoc;` helpers `object h(string)`, `string handle(object)`, `List<RhinoObject> q(string filter)`, `object ghdoc()`, `string args` (JSON text). Output with `Console.WriteLine`.

| Python | C# |
|---|---|
| `rg.Point3d(0,0,0)` | `new Point3d(0, 0, 0)` |
| `doc.Objects.Add(geo, a)` | `Guid id = doc.Objects.Add(geo, a);` |
| `None` check | `if (breps == null \|\| breps.Length == 0) …` |
| `h('r3').Geometry` | `((RhinoObject)h("r3")).Geometry` |
| loop with filter | `foreach (var o in q("layer:Beams")) …` |

Use C# when: heavy loops (speed), APIs needing generics/out params, or GH SDK work that is awkward from Python. Out params: `Intersection.CurveCurve(a, b, tol, tol)` returns events; `brep.Faces[0].TryGetPlane(out Plane pl)`.
