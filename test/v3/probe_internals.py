"""Setup discovery (not counted): full archive keys for Karamba Support buttons / Loads unit, and whether script
components accept code from plain Python (what the other systems' Python fallback can use)."""
import Grasshopper, System
from GH_IO.Serialization import GH_Archive
gd = Grasshopper.Instances.ActiveCanvas.Document
srv = Grasshopper.Instances.ComponentServer


def keys(obj, filt):
    a = GH_Archive(); a.CreateNewRoot(True); root = a.GetRootNode; obj.Write(root)
    out = []
    def walk(c, pre):
        for it in c.Items:
            if filt(it.Name): out.append(pre + it.Name + ("[%d]" % it.Index if it.Index >= 0 else "") + "=" + str(it.InternalData)[:30])
        for ch in c.Chunks: walk(ch, pre + ch.Name + ("[%d]" % ch.Index if ch.Index > 0 else "") + "/")
    walk(root, "")
    return out


for name, filt in [("Support", lambda n: "button" in n.lower()), ("Loads", lambda n: "unit" in n.lower())]:
    p = [x for x in srv.ObjectProxies if (x.Desc.Name == name or x.Desc.Name.startswith(name + " (")) and x.Desc.Category == "Karamba3D" and not x.Obsolete and x.Exposure != Grasshopper.Kernel.GH_Exposure.hidden]
    o = p[0].CreateInstance(); o.CreateAttributes()
    print(name, p[0].Desc.Name, keys(o, filt)[:8])

py = [x for x in srv.ObjectProxies if x.Desc.Name == "Python 3 Script"][0]
c = py.CreateInstance()
print("py3 guid", py.Guid, "SetSource public:", hasattr(c, "SetSource"), "methods:", [m for m in dir(c) if "Source" in m][:6])
cs = [x for x in srv.ObjectProxies if x.Desc.Name == "C# Script"][0]
c2 = cs.CreateInstance()
print("cs guid", cs.Guid, "SetSource public:", hasattr(c2, "SetSource"), "params-from-script:", [m for m in dir(c2) if "Parameter" in m and "Script" in m][:4])
