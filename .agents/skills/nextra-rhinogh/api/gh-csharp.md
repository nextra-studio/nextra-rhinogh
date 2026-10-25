---
description: Grasshopper × C# — Rhino 8 C# script components and the GH SDK
---
# GH × C#

## C# script component (Rhino 8) — ports come from the signature
```
cs = C# Script {code="""
using System; using System.Collections.Generic; using Rhino.Geometry; using Grasshopper.Kernel;
public class Script_Instance : GH_ScriptInstance
{
    private void RunScript(Curve crv, int count, List<double> hs, ref object towers)
    {
        // inputs: crv (item, Curve hint), count (int), hs (list, double); output: towers
        towers = …;
    }
}
"""}
```
- The engine parses `RunScript(...)`: params, access (`List<T>` = list, `DataTree<T>` = tree), type hints (Curve, int, double, Point3d, Brep, …) and `ref/out` outputs. Rhino 8 otherwise rewrites the signature from the params.
- Tree: `DataTree<Point3d> t = new DataTree<Point3d>(); t.Add(p, new GH_Path(0, i));`
- Messages: `Component.Message = "ok";` · `Component.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "…");` · `Print("…")` → `out`.
- Errors return as `… [line:col]` with the failing lines; fix with `gh_edit {script:{handle, patch}}`.

## SDK (from run lang:'cs' or C# script)
| task | code |
|---|---|
| canvas / doc | `Grasshopper.Instances.ActiveCanvas`, `.Document` (or `ghdoc()`) |
| create | `var o = Instances.ComponentServer.EmitObject(guid); o.CreateAttributes(); doc.AddObject(o, false);` (prefer shorthand) |
| wire | `dst.Params.Input[0].AddSource(src.Params.Output[0]);` |
| solve | `doc.NewSolution(false);` (never inside a solution; use `doc.ScheduleSolution(1, cb)`) |
| errors | `obj.RuntimeMessages(GH_RuntimeMessageLevel.Error)` |
| bounds | `obj.Attributes.Bounds` (canvas coords) |
