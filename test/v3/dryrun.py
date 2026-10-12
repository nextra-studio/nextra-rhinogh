"""Dry-run every v3 spec through Nextra shorthand (not counted) and print the result line + key data."""
import os, sys, json
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools")); sys.path.insert(0, HERE)
import nx, specs3

S = os.path.join(REPO, "test", "regress", "scripts")
V3 = os.path.join(HERE, "scripts")


def run(code, args=None):
    return nx.call("run", {"code": code, "args": args or {}}, 120, log=False)[0]["text"]


def reset():
    run("for o in list(doc.Objects): doc.Objects.Delete(o, True)\nfor i in range(doc.Layers.Count-1,-1,-1):\n    l = doc.Layers[i]\n    if not l.IsDeleted and l.Name != 'Default': doc.Layers.Purge(i, True)\nprint(0)")


def ids_of(t):
    line = next((l for l in t.splitlines() if l.startswith("ids:")), "ids:")
    return dict(x.split("=") for x in line[4:].split())


def data(ids, keys):
    return nx.call("query", {"scope": "gh", "filter": " ".join(ids[k] for k in keys), "level": "data", "items": 2}, 60, log=False)[0]["text"]


which = sys.argv[1:] or ["tower", "fins", "kbeam", "house", "trees", "kshell"]
sys.stdout.reconfigure(encoding="utf-8")
for w in which:
    if w == "fins":
        reset()
        run("li = doc.Layers.Add('Slab', System.Drawing.Color.Gray)\na = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = li\n"
            "doc.Objects.AddBrep(rg.Box(rg.Plane.WorldXY, rg.Interval(-300,18300), rg.Interval(-300,12300), rg.Interval(13750,14000)).ToBrep(), a)")
    if w == "house":
        reset()
        doc_m = "doc.ModelUnitSystem = Rhino.UnitSystem.Meters\n"
        run(open(os.path.join(S, "house_input.py"), encoding="utf-8").read())
        run(open(os.path.join(S, "solids_to_fe.py"), encoding="utf-8").read())
    spec = getattr(specs3, {"tower": "TOWER", "fins": "FINS", "kbeam": "KBEAM", "house": "HOUSE", "trees": "TREES", "kshell": "KSHELL"}[w])
    sh = specs3.shorthand(spec)
    t = nx.call("gh_edit", {"new": True, "ops": sh}, 120, log=False)[0]["text"]
    print(f"===== {w} ({len(sh) // 4} tok shorthand)")
    print("\n".join(l for l in t.splitlines() if l.startswith(("ok", "partial", "FAILED", "results", " L", "solved", " g")))[:1200])
