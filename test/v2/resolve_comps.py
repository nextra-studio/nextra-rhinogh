"""Run inside Rhino (setup only, not counted): for each (name, category) in args['comps'] print guid, whether the
name is ambiguous among installed components, and input/output nicknames in order (for index-based APIs)."""
import json
import Grasshopper
res = {}
proxies = [p for p in Grasshopper.Instances.ComponentServer.ObjectProxies if not p.Obsolete]
for name, cat in args["comps"]:
    same = [p for p in proxies if p.Desc.Name == name]
    pick = [p for p in same if (cat is None or p.Desc.Category.startswith(cat))]
    p = pick[0] if pick else (same[0] if same else None)
    if p is None:
        res[name + "|" + str(cat)] = None
        continue
    o = p.CreateInstance()
    ins = [x.NickName for x in o.Params.Input] if hasattr(o, "Params") else []
    outs = [x.NickName for x in o.Params.Output] if hasattr(o, "Params") else []
    tys = [x.TypeName for x in o.Params.Input] if hasattr(o, "Params") else []
    res[name + "|" + str(cat)] = {"guid": str(p.Guid), "ambiguous": len(same) > 1, "in": ins, "out": outs, "in_types": tys}
if args.get("out"):
    open(args["out"], "w").write(json.dumps(res))   # harness: no stdout size limit
    print("written")
else:
    print(json.dumps(res))
