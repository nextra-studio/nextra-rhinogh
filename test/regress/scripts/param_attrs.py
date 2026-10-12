import System
import Grasshopper as gh
from System.Drawing import PointF

d = ghdoc()
comps = [o for o in d.Objects if isinstance(o, gh.Kernel.IGH_Component)]
orphaned = [o.NickName for o in comps
            if any(not System.Object.ReferenceEquals(p.Attributes.Parent, o.Attributes)
                   for p in list(o.Params.Input) + list(o.Params.Output))]
print("orphaned: %d %s" % (len(orphaned), orphaned))


def layout(o):
    a = o.Attributes
    return [(round(a.Bounds.Width), round(a.Bounds.Height))] + [
        (round(p.Attributes.Bounds.X - a.Pivot.X), round(p.Attributes.Bounds.Y - a.Pivot.Y), round(p.Attributes.Bounds.Width))
        for p in o.Params.Input]


asm = [o for o in comps if o.NickName == "Assemble"][0]
native = gh.Instances.ComponentServer.EmitObjectProxy(asm.ComponentGuid).CreateInstance()
native.Attributes.Pivot = PointF(asm.Attributes.Pivot.X, asm.Attributes.Pivot.Y + 2000)
d.AddObject(native, False)
native.Attributes.ExpireLayout(); native.Attributes.PerformLayout()
print("assemble native: %s" % (layout(asm) == layout(native)))
d.RemoveObject(native, False)
