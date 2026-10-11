---
description: Rhino 8 × Python 3 (RhinoCommon) cheat sheet for `run`
---
# Rhino × Python (run)

Prelude already imported: `System, Rhino, scriptcontext as sc, Rhino.Geometry as rg, Grasshopper`; `doc = sc.doc`; helpers `h('r12')` → RhinoObject (GH: `h('g4')`), `handle(obj|guid)` → 'r12', `q('layer:X type:curve')` → [RhinoObject], `ghdoc()`, `args` (dict from the call). Print what the next step needs; deltas are reported for you.

| task | code |
|---|---|
| point/vector/plane | `rg.Point3d(x,y,z)`, `rg.Vector3d`, `rg.Plane(o, xaxis, yaxis)`, `rg.Plane.WorldXY` (copy before editing) |
| curves | `rg.Line(a,b)`, `rg.Polyline(pts).ToNurbsCurve()`, `rg.NurbsCurve.Create(False,3,pts)`, `rg.Circle(pl,r)` |
| solids | `rg.Box(pl, ix, iy, iz).ToBrep()`, `rg.Extrusion.Create(crv, h, True)`, `rg.Brep.CreateFromLoft(crvs, rg.Point3d.Unset, rg.Point3d.Unset, rg.LoftType.Normal, False)` |
| booleans | `rg.Brep.CreateBooleanUnion(breps, doc.ModelAbsoluteTolerance)` → None/[] on failure: check |
| transform | `xf = rg.Transform.Translation(v)` / `.Rotation(math.radians(a), axis, c)` / `.Scale(c, f)`; `doc.Objects.Transform(obj, xf, True)` |
| add with layer/name | `a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = idx; a.Name = 'C1'; doc.Objects.Add(geo, a)` |
| layer | `i = doc.Layers.FindByFullPath('Columns', -1); i = i if i >= 0 else doc.Layers.Add('Columns', System.Drawing.Color.Red)` |
| replace / delete | `doc.Objects.Replace(obj.Id, geo)`, `doc.Objects.Delete(obj, True)` |
| user text | `obj.Attributes.SetUserString('k','v'); obj.CommitChanges()` |
| oriented box | `bb = geo.GetBoundingBox(plane)`; principal axes: `rg.VolumeMassProperties.Compute(brep).CentroidCoordinatesPrincipalMoments` |
| sections | `rg.Brep.CreateContourCurves(brep, p0, p1, step)`, `rg.Intersect.Intersection.BrepPlane(b, pl, tol)` |
| mesh | `rg.Mesh.CreateFromBrep(b, rg.MeshingParameters.FastRenderMesh)` |
| units | `doc.ModelUnitSystem`, tolerance `doc.ModelAbsoluteTolerance` |

Pitfalls: angles in radians · check None from geometry ops · `Plane.WorldXY` is a value type · loft seams/direction must match · large loops: print progress counts, not geometry.
