"""Smoke test of the engine's `ops` tool (typed function menu) on a live Rhino: every function once, checked by its reply.
usage: python test/assistant/ops_smoke.py   (needs Rhino with NextraRhinoGh; works in a new empty document)"""
import json, os, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
import nx  # noqa: E402


def nxc(tool, args):
    return nx.call(tool, args, log=False)[0]

fails = []


def call(fn, args=None, targets=None, expect=None, ok=True):
    res = nxc("ops", {"action": "run", "fn": fn, "args": args or {}, "targets": targets or []})
    text = res.get("text", "")
    good = res.get("ok") == ok and (expect is None or expect in text)
    print(("PASS " if good else "FAIL ") + fn + ": " + text.replace("\n", " | ")[:160])
    if not good:
        fails.append(fn)
    return text


def handles(layer=None):
    els = json.loads(nxc("ops", {"action": "elements"})["text"])["elements"]
    return [e["h"] for e in els if layer is None or e["layer"] == layer]


nxc("run", {"code": "import Rhino\nRhino.RhinoDoc.ActiveDoc.Objects.Clear()\nfor o in list(Rhino.RhinoDoc.ActiveDoc.Layers):\n    pass\nprint('cleared')"})
menu = json.loads(nxc("ops", {"action": "list"})["text"])
print(f"menu: {len(menu['functions'])} functions, {len(menu['workflows'])} workflows, unit {menu['unit']}")

call("frame_building", {"nx": 3, "ny": 2, "sx": 6000, "sy": 5000, "floors": 3, "storey": 3500}, expect="6 columns and 3 slabs")
call("ask_model", {"about": "counts"}, expect="9 objects")
cols = handles("Columns")
call("ask_object", {"about": "volume"}, cols[:1], expect="volume")
call("select", {}, cols, expect="selected 6")
call("move", {"by": [0, 0, 100]}, [], expect="moved 6")   # the selection
call("copy", {"by": [20000, 0, 0]}, cols[:2], expect="copied 2")
call("rotate", {"angle": 45}, cols[:1], expect="rotated 1")
call("scale", {"factor": 2}, cols[:1], expect="scaled 1")
call("mirror", {"across": "x"}, cols[:1], expect="mirrored 1")
call("array_linear", {"count": 3, "step": [0, 3000, 0]}, cols[:1], expect="3 copies")
call("array_polar", {"count": 5}, cols[:1], expect="5 copies")
call("add_rectangle", {"width": 2000, "depth": 1000}, expect="rectangle")
call("add_circle", {"radius": 500, "at": [5000, 5000, 0]}, expect="circle")
call("add_line", {"points": [[0, 0, 0], [1000, 0, 0], [1000, 1000, 0]]}, expect="polyline")
crv = [e["h"] for e in json.loads(nxc("ops", {"action": "elements"})["text"])["elements"] if e["type"].startswith(("circle", "curve", "polyline", "rect", "closed", "line"))]
call("offset_curve", {"distance": 100, "side": "out"}, crv[:1], expect="offset 1")
call("extrude", {"height": 3000}, crv[:1], expect="extruded 1")
call("add_layer", {"name": "Facade", "colour": "orange"}, expect="Facade")
call("set_layer", {"layer": "Facade"}, cols[:2], expect="to layer Facade")
call("set_colour", {"colour": "red"}, cols[:2], expect="red")
call("rename", {"name": "C1"}, cols[:1], expect="named 1")
call("group", {}, cols[2:4], expect="grouped 2")
call("hide", {}, cols[4:5], expect="hid 1")
call("show_all", {}, expect="showed 1")
call("lock", {}, cols[5:6], expect="locked 1")
call("unlock_all", {}, expect="unlocked 1")
call("align", {"to": "left"}, cols[:3], expect="aligned 3")
call("move_to_origin", {"by": "corner"}, cols[:1], expect="moved")
call("add_layer", {"name": "Empty1"})
call("add_layer", {"name": "Empty2"})
call("set_current_layer", {"layer": "Default"}, expect="current layer Default")
call("delete_empty_layers", {}, expect="Empty1")
call("rename_layer", {"layer": "Facade", "name": "Facade_A"}, expect="Facade_A")
call("layer_colour", {"layer": "Facade_A", "colour": "blue"}, expect="blue")
call("lock_layer", {"layer": "Facade_A", "locked": True}, expect="locked")
call("lock_layer", {"layer": "Facade_A", "locked": False}, expect="unlocked")
call("isolate_layer", {"layer": "Slabs"}, expect="only Slabs")
call("show_all_layers", {}, expect="layers on")
call("merge_layers", {"from": "Facade_A", "into": "Columns"}, expect="layer removed")
call("copy", {"by": [0, 0, 0]}, cols[:1], expect="copied 1")      # an exact duplicate
call("delete_duplicates", {}, expect="duplicates")   # the mirror of a symmetric box is a duplicate too
call("delete_small", {"size": 1}, expect="nothing smaller")
call("sort_by_type", {}, expect="sorted under Sorted")
call("delete", {}, cols[-1:], expect="deleted 1")
call("move", {"by": [1, 0, 0]}, ["r999999"], ok=False)              # bad handle -> refused, nothing changed
call("unselect_all", {}, expect="cleared")
call("ask_model", {"about": "layers"}, expect="Sorted")
print(f"\n{'ALL PASSED' if not fails else 'FAILED: ' + ', '.join(fails)}")
sys.exit(1 if fails else 0)
