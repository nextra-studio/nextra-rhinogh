"""Run inside Rhino (setup only, not counted): for each (name, category[, ports]) in args['comps'] return guid, the full
installed name, whether the name is ambiguous, and input/output nicknames in order (for index-based APIs).
When ports are given, the variant whose inputs include all of them is chosen (several plug-ins ship same-named
components, e.g. Karamba's Support / Support Agent)."""
import json
import Grasshopper
res = {}
proxies = [p for p in Grasshopper.Instances.ComponentServer.ObjectProxies if not p.Obsolete]


def ports_of(p):
    try:
        o = p.CreateInstance()
        return o, ([x.NickName for x in o.Params.Input] if hasattr(o, "Params") else [])
    except Exception:
        return None, []


for entry in args["comps"]:
    name, cat = entry[0], entry[1]
    want = entry[2] if len(entry) > 2 else []
    # exact names AND plug-in names with a suffix ("Support (Karamba3D)" next to another plug-in's "Support")
    same = [p for p in proxies if p.Desc.Name.strip() == name or p.Desc.Name.strip().split(" (")[0] == name]
    pick = [p for p in same if (cat is None or p.Desc.Category.startswith(cat))] or same
    if want:
        fit = [p for p in pick if all(w in ports_of(p)[1] for w in want)]
        pick = fit or pick
    vis = [p for p in pick if p.Exposure != Grasshopper.Kernel.GH_Exposure.hidden]
    p = (vis or pick or [None])[0]
    key = name + "|" + str(cat) + "|" + ",".join(want)
    if p is None:
        res[key] = None
        continue
    o = p.CreateInstance()
    ins = [x.NickName for x in o.Params.Input] if hasattr(o, "Params") else []
    outs = [x.NickName for x in o.Params.Output] if hasattr(o, "Params") else []
    tys = [x.TypeName for x in o.Params.Input] if hasattr(o, "Params") else []
    res[key] = {"guid": str(p.Guid), "name": p.Desc.Name, "ambiguous": len(same) > 1, "in": ins, "out": outs, "in_types": tys}
if args.get("out"):
    open(args["out"], "w").write(json.dumps(res))   # harness: no stdout size limit
    print("written")
else:
    print(json.dumps(res))
