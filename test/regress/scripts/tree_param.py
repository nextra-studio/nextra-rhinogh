"""Number param with an explicit data tree: {0;0}: 1,2,3  {0;1}: 10,20  (shorthand set=[...] only makes flat lists)"""
import System
from Grasshopper.Kernel.Parameters import Param_Number
from Grasshopper.Kernel.Data import GH_Path, GH_Structure
from Grasshopper.Kernel.Types import GH_Number
d = ghdoc()
p = Param_Number(); p.NickName = "Tree data"; p.CreateAttributes()
p.Attributes.Pivot = System.Drawing.PointF(0, 900)
tree = GH_Structure[GH_Number]()
for path, vals in [((0, 0), [1, 2, 3]), ((0, 1), [10, 20])]:
    for v in vals:
        tree.Append(GH_Number(v), GH_Path(System.Array[int](path)))
p.SetPersistentData(tree)
d.AddObject(p, False)
d.NewSolution(False)
print(handle(p), p.VolatileData.PathCount, "branches")
