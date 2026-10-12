"""Benchmark v3: every v1 case (01–10, 12–16) + new cases 17–20 on Nextra, McNeel Rhino-MCP-Platform and RhinoMCP.

Counting is the same as v2 (out = tool-call arguments the model writes, in = results it reads, plus fixed context).
Each case runs on all three systems in turn from the same starting state; a neutral check (not counted) follows.
Where a system has no native tool for a step it uses its own Python tool (marked 'py' in the rows) with the code a
capable model would write — that code is counted.

usage: python test/v3/bench_v3.py [01 05 ...]      (default: all)
"""
import json, os, re, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools")); sys.path.insert(0, HERE); sys.path.insert(0, os.path.join(REPO, "test", "v2"))
import nx  # noqa: E402
import bench_v2 as V2  # noqa: E402  (clients, token counting, McNeel recovery)
import specs3 as SP  # noqa: E402
import pyhelpers as PY  # noqa: E402

V2.OUT = os.path.join(HERE, "out")
ROWS = V2.ROWS
RS = os.path.join(REPO, "test", "regress", "scripts")
V3S = os.path.join(HERE, "scripts")
TMP = os.path.join(HERE, "_tmp")
MC_PRE, RM_PRE, SUMMARY = V2.MC_PRE, V2.RM_PRE, V2.SUMMARY


def read(*p):
    with open(os.path.join(*p), encoding="utf-8") as f:
        return f.read()


def nrun(code, args=None, timeout=120):
    return nx.call("run", {"code": code, "args": args or {}}, timeout, log=False)[0]["text"]


def nquery(args):
    return nx.call("query", args, 60, log=False)[0]["text"]


def reset_rhino(units="Millimeters"):
    nrun("for o in list(doc.Objects): doc.Objects.Delete(o, True)\nfor i in range(doc.Layers.Count-1,-1,-1):\n    l = doc.Layers[i]\n"
         "    if not l.IsDeleted and l.Name != 'Default': doc.Layers.Purge(i, True)\n"
         f"doc.ModelUnitSystem = Rhino.UnitSystem.{units}\nprint(0)")


def counts():
    t = nrun("from collections import Counter\nprint(dict(Counter(doc.Layers[o.Attributes.LayerIndex].Name for o in doc.Objects)))")
    return next((l for l in t.splitlines() if l.startswith("{")), t[:200])


def gh_counts(names):
    # one solve first: a tool may leave the canvas expired (GH would solve on the next canvas event anyway)
    t = nrun("import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\nd.NewSolution(False)\nres = {}\n"
             f"for o in d.Objects:\n    if o.Name.strip().split(' (')[0] in {json.dumps(names)} and hasattr(o, 'Params'):\n"
             "        res[o.Name.strip().split(' (')[0]] = res.get(o.Name.strip().split(' (')[0], 0) + sum(p.VolatileDataCount for p in o.Params.Output)\n"
             "print('groups', sum(1 for o in d.Objects if o.GetType().Name == 'GH_Group'), 'errors', sum(1 for o in d.Objects if hasattr(o, 'RuntimeMessageLevel') and str(o.RuntimeMessageLevel) == 'Error'), json.dumps(res))\n".replace("json.dumps", "str"))
    return next((l for l in t.splitlines() if l.startswith("groups")), t[:200])


def gh_value(comp_name, port):
    """first value of an output (neutral), e.g. Karamba MaxDisp"""
    t = nrun("import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\n"
             f"vals = [str(p.VolatileData.AllData(True).__iter__().__next__()) for o in d.Objects if o.Name.strip().split(' (')[0] == '{comp_name}' and hasattr(o, 'Params') for p in o.Params.Output if p.NickName == '{port}' and p.VolatileDataCount > 0]\n"
             "print('VAL', vals)")
    return next((l for l in t.splitlines() if l.startswith("VAL")), t[:200])


def new_canvas():
    nx.call("gh_edit", {"new": True}, 60, log=False)


# ----------------------------------------------------------------- generic GH build per system

def ids_line(t):
    line = next((l for l in t.splitlines() if l.startswith("ids:")), "ids:")
    return dict(x.split("=") for x in line[4:].split())


def helpers_for(spec, values, groups, extra=()):
    parts = [PY.FLAGS]
    if spec["state"]: parts.append(PY.STATE)
    if spec["menu"]: parts.append(PY.MENU)
    if spec["refs"]: parts.append(PY.REF)
    if groups: parts.append(PY.GROUP)
    return parts + list(extra)


def gh_build(S, spec, case, docs=False):
    """Build a spec from a fresh canvas the way each system must. Returns key → id/handle map."""
    c = S.call
    info = V2.resolve(spec) if S.name != "nextra" else None
    if S.name == "nextra":
        if docs: c(case, "docs", "query", {"scope": "docs", "filter": "shorthand"})
        t = c(case, "build", "gh_edit", {"new": True, "ops": SP.shorthand(spec)})
        return ids_line(t)
    # names the model must look up first: ambiguous ones (to get a GUID) and plug-in ones whose real name carries a suffix
    ambiguous = sorted({SP.by_key(spec)[k]["t"] for k, i in info.items() if i and (i["ambiguous"] or i["name"].strip() != SP.by_key(spec)[k]["t"])})
    late = spec.get("late", [])
    if S.name == "mcneel":
        c(case, "build", "g1_clear_canvas", {"confirm": True})
        for a in ambiguous:
            if a not in V2.LEARNED: c(case, "search", "g1_search_components", {"query": a, "limit": 5})
        sel = lambda n, i: V2.LEARNED.get(n["t"], i["guid"] if i["ambiguous"] else i["name"])
        graph = SP.mcneel_graph(spec, info, sel)
        t = c(case, "build", "g1_apply_graph", graph)
        ids = V2.ids_from(t, [n["k"] for n in spec["nodes"]])
        ids.update(V2.mcneel_recover(S, case, spec, graph, t, ids))
        body = SP.fix_body(spec, ids)
        c(case, "fix", "run_python", {"script": PY.gh(*helpers_for(spec, True, True), body=body)})
        if late:
            W = lambda s, sp, d, dp: {"SrcId": ids[s], "Src": "0" if SP.by_key(spec)[s].get("slider") or SP.special(info, s) else (sp or info[s]["out"][0]), "DstId": ids[d], "Dst": dp}
            c(case, "late", "g1_connect_many", {"wires": [W(s, sp, d, dp) for s, sp, d, dp, fl in late], "solve": True})
        c(case, "verify", "g1_get_canvas_graph", {"include_data": True, "sample_size": 2})
        return ids
    # RhinoMCP
    c(case, "build", "gh_clear_canvas", {})
    for a in ambiguous:
        c(case, "search", "gh_search_components", {"query": a, "limit": 5})
    g, py_values = SP.rhinomcp_graph(spec, info)
    t = c(case, "build", "gh_build_graph", g)
    ids = V2.ids_from(t, [n["k"] for n in spec["nodes"]])
    gid = re.search(r'"graph_id":\s*"([^"]+)"', t)
    body = SP.fix_body(spec, ids, values=False, groups=False, extra_values=py_values)
    fixed = False
    if body.strip():
        c(case, "fix", "execute_rhinoscript_python_code", {"code": PY.gh(*helpers_for(spec, False, False), body=body)}); fixed = True
    if late:
        ops = [{"op": "connect", "source": s, **({"source_output_name": sp} if sp else {}), "target": d, "target_input_name": dp} for s, sp, d, dp, fl in late]
        c(case, "late", "gh_mutate_graph", {"operations": ops + [{"op": "recompute"}]}); fixed = True
    if fixed and gid:   # the build reply's data is stale after fixes: read the results once
        c(case, "verify", "gh_get_graph", {"graph_id": gid.group(1), "include_values": True, "max_items": 3})
    return ids


# ----------------------------------------------------------------- cases

def rhino_code(S, case, task, code, args=None, save_as=None):
    """Run the same model script on each system (Nextra: run + library; others: their Python + a printed summary)."""
    if S.name == "nextra":
        a = {"code": code, "args": args or {}}
        if save_as: a["save_as"] = save_as
        return S.call(case, task, "run", a)
    pre = (MC_PRE if S.name == "mcneel" else RM_PRE) + f"args = {json.dumps(args or {})}\n"
    tool = "run_python" if S.name == "mcneel" else "execute_rhinoscript_python_code"
    key = "script" if S.name == "mcneel" else "code"
    return S.call(case, task, tool, {key: pre + code + SUMMARY})


def py_call(S, case, task, code):
    """Python on the other systems (Rhino-side code, no summary)."""
    if S.name == "mcneel":
        return S.call(case, task, "run_python", {"script": MC_PRE + code})
    return S.call(case, task, "execute_rhinoscript_python_code", {"code": RM_PRE + code})


def gh_py(S, case, task, code):
    """Grasshopper-side Python on the other systems."""
    if S.name == "mcneel":
        return S.call(case, task, "run_python", {"script": code})
    return S.call(case, task, "execute_rhinoscript_python_code", {"code": code})


def look(S, case, view="iso"):
    if S.name == "nextra": return S.call(case, "look", "view", {"view": view})
    if S.name == "mcneel": return S.call(case, "look", "get_viewport_image", {"view": "perspective" if view == "iso" else view, "displayMode": "Shaded", "width": 800, "height": 600})
    return S.call(case, "look", "capture_viewport", {"viewport": "perspective" if view == "iso" else view, "width": 800, "height": 600, "zoom_to_fit": True})


def undo(S, case, ck_text):
    if S.name == "nextra":
        ck = re.search(r"checkpoint (c\d+)", ck_text)
        return S.call(case, "undo", "undo", {"checkpoint": ck.group(1)} if ck else {"steps": 1})
    if S.name == "mcneel": return S.call(case, "undo", "run_command", {"command": "_Undo"})
    return S.call(case, "undo", "undo", {"steps": 1})


FRAME = None


def c01(S):
    reset_rhino()
    rhino_code(S, "01", "build", FRAME, {"storeys": 3}, save_as="frame_building")
    r = counts()
    look(S, "01")
    return {"model": r}


def c02(S):
    reset_rhino()
    nrun(FRAME, {"storeys": 3})                       # same starting model for everyone (not counted)
    if S.name == "nextra":
        S.call("02", "add_storey", "run", {"lib": "frame_building", "args": {"storeys": 1, "z0": 10500}})
        t = S.call("02", "move", "run", {"code": "xf = rg.Transform.Translation(3000, 0, 0)\nfor o in q('name:*_L4'): doc.Objects.Transform(o, xf, True)"})
    else:
        rhino_code(S, "02", "add_storey", FRAME, {"storeys": 1, "z0": 10500})
        t = py_call(S, "02", "move", "xf = rg.Transform.Translation(3000, 0, 0)\nn = 0\nfor o in list(doc.Objects):\n    if (o.Attributes.Name or '').endswith('_L4'):\n        doc.Objects.Transform(o, xf, True); n += 1\nprint('moved', n)")
    slab = lambda: nquery({"filter": "name:S_L4"}).splitlines()[-2:]
    moved = slab()
    undo(S, "02", t)
    return {"after_move": str(moved), "after_undo": str(slab()), "model": counts()}


def c03(S):
    new_canvas()
    ids = gh_build(S, SP.TOWER, "03", docs=True)
    CTX[S.name + "03"] = ids
    built = gh_counts(["Cap Holes", "Loft"])
    edited = c04(S)                      # case 04 edits this system's own case-03 canvas (rows labelled 04)
    return {"gh": built, "case04": edited}


def c04(S):
    ids = CTX.get(S.name + "03")
    if not ids:
        return {"skipped": "case 03 did not run"}
    if S.name == "nextra":
        S.call("04", "edit", "gh_edit", {"ops": f"- {ids['rec']}\n## Floors\ncir = Circle (P={ids['pl']}, R={ids['w']}^e(x/2))\n{ids['rot']} (G=cir)"})
    elif S.name == "mcneel":
        t = S.call("04", "edit", "g1_apply_graph", {"sliders": [], "components": [{"Key": "cir", "Selector": V2.LEARNED.get("Circle", V2.info_guid("Circle", "Curve")), "X": 700, "Y": 0}], "wires": [], "solve": False})
        new = V2.ids_from(t, ["cir"])
        S.call("04", "edit", "g1_connect_many", {"wires": [{"SrcId": ids["pl"], "Src": "P", "DstId": new["cir"], "Dst": "P"}, {"SrcId": ids["w"], "Src": "0", "DstId": new["cir"], "Dst": "R"}], "solve": False})
        gh_py(S, "04", "edit", PY.gh(PY.FLAGS, body=f"expr('{new['cir']}', 'R', 'x/2')\nr = inp('{ids['rot']}', 'G'); r.RemoveAllSources(); r.AddSource(o('{new['cir']}').Params.Output[0])\ngd.RemoveObject(o('{ids['rec']}'), False)"))
    else:
        S.call("04", "edit", "gh_mutate_graph", {"operations": [
            {"op": "delete", "target": "rec"}, {"op": "create", "alias": "cir", "component_guid": V2.info_guid("Circle", "Curve")},
            {"op": "connect", "source": "pl", "target": "cir", "target_input_name": "P"}, {"op": "connect", "source": "w", "target": "cir", "target_input_name": "R"},
            {"op": "connect", "source": "cir", "target": "rot", "target_input_name": "G"}, {"op": "recompute"}]})
        gh_py(S, "04", "edit", PY.gh(PY.FLAGS, body="c = [x for x in gd.Objects if x.Name == 'Circle'][0]\nexpr(str(c.InstanceGuid), 'R', 'x/2')"))
    return {"gh": gh_counts(["Cap Holes", "Circle", "Rectangle"])}


SLAB = ("li = doc.Layers.Add('Slab', System.Drawing.Color.Gray)\na = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = li\n"
        "doc.Objects.AddBrep(rg.Box(rg.Plane.WorldXY, rg.Interval(-300,18300), rg.Interval(-300,12300), rg.Interval(13750,14000)).ToBrep(), a)\nprint('slab')")


def c05(S):
    reset_rhino(); new_canvas()
    if S.name == "nextra": S.call("05", "slab", "run", {"code": SLAB})
    else: py_call(S, "05", "slab", SLAB)
    ids = gh_build(S, SP.FINS, "05")
    if S.name == "nextra":
        S.call("05", "bake", "gh_edit", {"bake": [ids["ext"]], "layer": "Fins"})
    else:
        gh_py(S, "05", "bake", PY.gh(PY.BAKE, body=f"bake('{ids['ext']}', 'Fins')"))
    return {"gh": gh_counts(["Extrude"]), "model": counts()}


PY_CODE = ("import math\nimport Rhino.Geometry as rg\npts = [rg.Point3d(r*math.cos(2*math.pi*i/n), r*math.sin(2*math.pi*i/n), 0) for i in range(int(n))]\n"
           "crv = rg.Polyline(pts + [pts[0]]).ToNurbsCurve()\nprint('made', len(pts))\n")
PY_BROKEN = PY_CODE + "len_total = crv.GetLength() / undefined_var\n"
PY_FIXED = PY_CODE + "len_total = crv.GetLength() / 1000.0\n"


def sliders_native(S, case, sl):
    """Place sliders with the system's own tool; returns key → id."""
    if S.name == "mcneel":
        t = S.call(case, "sliders", "g1_apply_graph", {"sliders": [{"Key": k, "Min": a, "Value": v, "Max": b, "Type": "int", "Name": n, "X": 0, "Y": 60 * i} for i, (k, v, a, b, n) in enumerate(sl)], "components": [], "wires": [], "solve": False})
    else:
        t = S.call(case, "sliders", "gh_build_graph", {"components": [{"alias": k, "component_name": "Number Slider", "value": v, "min": a, "max": b, "decimals": 0, "nickname": n} for k, v, a, b, n in sl]})
    return V2.ids_from(t, [s[0] for s in sl])


def c06(S):
    new_canvas()
    if S.name == "nextra":
        t = S.call("06", "build", "gh_edit", {"ops": '## Py demo #ABDBFF\ncnt = Number Slider {v=8, r=3..30, int} "Count"\nrad = Number Slider {v=5000, r=1000..20000, int} "Radius"\n'
                                                  f'py = Python 3 Script (n=cnt, r=rad) -> (pts, crv) {{code="""\n{PY_CODE}"""}}'})
        ids = ids_line(t)
        S.call("06", "break", "gh_edit", {"script": {"handle": ids["py"], "patch": [{"find": "print('made', len(pts))", "replace": "print('made', len(pts))\nlen_total = crv.GetLength() / undefined_var"}]}})
        S.call("06", "fix", "gh_edit", {"script": {"handle": ids["py"], "patch": [{"find": "/ undefined_var", "replace": "/ 1000.0"}]}})
    else:
        sid = sliders_native(S, "06", [("cnt", 8, 3, 30, "Count"), ("rad", 5000, 1000, 20000, "Radius")])
        body = (f"c = script('py', 400, 0, [('n', 'item'), ('r', 'item')], ['pts', 'crv'], {json.dumps(PY_CODE)})\n"
                f"c.Params.Input[0].AddSource(o('{sid['cnt']}')); c.Params.Input[1].AddSource(o('{sid['rad']}'))\n"
                "gd.NewSolution(True)\nprint(str(c.InstanceGuid), [p.NickName + ':' + str(p.VolatileDataCount) for p in c.Params.Output], errors(c))")
        t = gh_py(S, "06", "build", PY.gh(PY.SCRIPT, body=body))
        m = re.search(r"([0-9a-f]{8}-[0-9a-f-]{27})", t)
        if not m: return {"failed": t[:200]}
        cid = m.group(1)
        chk = f"\ngd.NewSolution(True)\nprint([p.NickName + ':' + str(p.VolatileDataCount) for p in o('{cid}').Params.Output], errors(o('{cid}')))"
        gh_py(S, "06", "break", PY.gh(PY.SCRIPT, body=f"set_code(o('{cid}'), {json.dumps(PY_BROKEN)})" + chk))
        gh_py(S, "06", "fix", PY.gh(PY.SCRIPT, body=f"set_code(o('{cid}'), {json.dumps(PY_FIXED)})" + chk))
    return {"gh": gh_counts(["Python 3 Script"])}


CS_CODE = '''using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Grasshopper.Kernel;

public class Script_Instance : GH_ScriptInstance
{
    private void RunScript(int count, double height, ref object towers)
    {
        var res = new List<Brep>();
        for (int i = 0; i < count; i++)
        {
            var c = new Circle(new Plane(new Point3d(i * 3000, 0, 0), Vector3d.ZAxis), 600);
            res.Add(Extrusion.Create(c.ToNurbsCurve(), height, true).ToBrep());
        }
        towers = res;
    }
}
'''


def c07(S):
    new_canvas()
    if S.name == "nextra":
        S.call("07", "build", "gh_edit", {"ops": f'## C# demo #FFD6AB\ncnt = Number Slider {{v=8, r=1..30, int}} "Count"\nh = Number Slider {{v=9000, r=1000..30000, int}} "Tower h"\n'
                                                 f'cs = C# Script (count=cnt, height=h) {{code="""\n{CS_CODE}"""}}'})
    else:
        sid = sliders_native(S, "07", [("cnt", 8, 1, 30, "Count"), ("h", 9000, 1000, 30000, "Tower h")])
        body = (f"c = script('cs', 400, 0, [('count', 'item'), ('height', 'item')], ['towers'], {json.dumps(CS_CODE)})\n"
                f"c.Params.Input[0].AddSource(o('{sid['cnt']}')); c.Params.Input[1].AddSource(o('{sid['h']}'))\n"
                "gd.NewSolution(True)\nprint(str(c.InstanceGuid), [p.NickName + ':' + str(p.VolatileDataCount) for p in c.Params.Output], errors(c))")
        t = gh_py(S, "07", "build", PY.gh(PY.SCRIPT, body=body))
        # Rhino 8 regenerates RunScript from the params: untyped inputs become `object` → compile errors
        # ("operator '<' cannot be applied to int and object"). The model must add type hints and set the code again.
        m = re.search(r"([0-9a-f]{8}-[0-9a-f-]{27})", t)
        if m:
            cid = m.group(1)
            fix = (f"c = o('{cid}')\nc.Params.Input[0].TypeHints.Select('int'); c.Params.Input[1].TypeHints.Select('double')\n"
                   f"set_code(c, {json.dumps(CS_CODE)})\ngd.NewSolution(True)\nprint([p.NickName + ':' + str(p.VolatileDataCount) for p in c.Params.Output], errors(c))")
            gh_py(S, "07", "type_hints", PY.gh(PY.SCRIPT, body=fix))
    return {"gh": gh_counts(["C# Script"])}


RADIO = None
CLICK_PY = """def click(i, fx, fy):
    from Grasshopper.GUI import GH_CanvasMouseEvent
    import System.Windows.Forms as F
    c = o(i); cv = Grasshopper.Instances.ActiveCanvas; b = c.Attributes.Bounds
    pt = cv.Viewport.ProjectPoint(System.Drawing.PointF(b.X + fx * b.Width, b.Y + fy * b.Height))
    e = GH_CanvasMouseEvent(cv.Viewport, F.MouseEventArgs(F.MouseButtons.Left, 1, int(pt.X), int(pt.Y), 0))
    c.Attributes.RespondToMouseDown(cv, e); c.Attributes.RespondToMouseUp(cv, e); c.ExpireSolution(True)
"""


def c08(S):
    new_canvas()
    if S.name == "nextra":
        t = S.call("08", "build", "gh_edit", {"ops": f'## Material picker #FFD6AB\nradio = Python 3 Script -> (choice, index) {{code=@{os.path.join(RS, "radio_component.py").replace(os.sep, "/")}}}'})
        rid = ids_line(t)["radio"]
        S.call("08", "click", "gh_edit", {"ops": f'{rid} {{click="0.5,0.80"}}'})
    else:
        body = (f"c = script('py', 300, 0, [], ['choice', 'index'], {json.dumps(RADIO)})\ngd.NewSolution(True)\n"
                "print(str(c.InstanceGuid), [str(list(p.VolatileData.AllData(True))[0]) for p in c.Params.Output if p.VolatileDataCount > 0], errors(c))")
        t = gh_py(S, "08", "build", PY.gh(PY.SCRIPT, body=body))
        m = re.search(r"([0-9a-f]{8}-[0-9a-f-]{27})", t)
        if not m: return {"failed": t[:200]}
        cid = m.group(1)
        gh_py(S, "08", "click", PY.gh(CLICK_PY, body=f"click('{cid}', 0.5, 0.80)\ngd.NewSolution(True)\nprint([str(x) for p in o('{cid}').Params.Output for x in p.VolatileData.AllData(True)][:3])"))
    return {"choice": gh_value("Python 3 Script", "choice")}


HELLO = '"""Points on a line. args: n"""\nfor i in range(args.get("n", 3)):\n    doc.Objects.AddPoint(rg.Point3d(i * 1000, 0, 0))\n'


def c09(S):
    path = os.path.join(TMP, f"hello_pts_{S.name}.py").replace(os.sep, "/")
    if os.path.exists(path): os.remove(path)
    if S.name == "nextra":
        S.call("09", "save", "memory", {"action": "save_script", "name": "hello_pts", "code": HELLO, "doc": "points on a line"})
        S.call("09", "list", "query", {"scope": "library", "filter": "hello"})
        return {"saved": "hello_pts" in nquery({"scope": "library", "filter": "hello"})}
    py_call(S, "09", "save", f"import os\nd = os.path.dirname({json.dumps(path)})\nif not os.path.isdir(d): os.makedirs(d)\nopen({json.dumps(path)}, 'w').write({json.dumps(HELLO)})\nprint('saved')")
    return {"saved": os.path.exists(path)}


def c10(S):
    reset_rhino()
    path = os.path.join(V3S, "frame_building.py").replace(os.sep, "/")
    if S.name == "nextra":
        S.call("10", "run_file", "run", {"file": path, "args": {"storeys": 2}})
    else:
        py_call(S, "10", "run_file", f"args = {{'storeys': 2}}\nexec(open({json.dumps(path)}).read())" + SUMMARY)
    return {"model": counts()}


def c12(S):
    new_canvas()
    gh_build(S, SP.KBEAM, "12")
    return {"max_disp_cm": gh_value("Analyse", "MaxDisp")}


def c13(S):
    reset_rhino()
    rhino_code(S, "13", "build", read(RS, "mbs.py"), {})
    look(S, "13", "iso")
    return {"model": counts()}


# The FE script uses Nextra's run helpers q() (filter selection) and obb() (oriented box + shape class).
# On the other systems the model has to write substitutes first; for this axis-aligned house a world-box version is enough.
PLAIN_HELPERS = '''def q(f):
    pat = f.split(":", 1)[1].rstrip("*")
    return [o for o in doc.Objects if doc.Layers[o.Attributes.LayerIndex].FullPath.startswith(pat)]
def obb(o):
    bb = o.Geometry.GetBoundingBox(True); d = bb.Diagonal
    axes = sorted([(d.X, rg.Vector3d.XAxis), (d.Y, rg.Vector3d.YAxis), (d.Z, rg.Vector3d.ZAxis)], key=lambda a: -a[0])
    L, W, H = axes[0][0], axes[1][0], axes[2][0]
    shape = "linear" if W < 0.25 * L and H < 0.25 * L else "planar" if H < 0.15 * W else "block"
    return {"shape": shape, "L": L, "W": W, "H": H, "axis": axes[0][1], "short": axes[2][1], "center": bb.Center}
def handle(o):
    return str(o.Id)[:8]
'''


def c14(S):
    reset_rhino(); new_canvas()
    rhino_code(S, "14", "house", read(RS, "house_input.py"), {})
    fe = read(RS, "solids_to_fe.py")
    rhino_code(S, "14", "solids_to_fe", fe if S.name == "nextra" else PLAIN_HELPERS + fe, {})
    gh_build(S, SP.HOUSE, "14")
    return {"mass_kg": gh_value("Assemble Model", "Mass"), "max_disp_cm": gh_value("Analyse", "MaxDisp"), "model": counts()}


TREE_PY = read(RS, "tree_param.py")


def c15(S):
    new_canvas()
    if S.name == "nextra":
        t = S.call("15", "tree", "run", {"code": TREE_PY, "label": "explicit data tree"})
        hd = re.search(r"(g\d+) 2 branches", t).group(1)
        S.call("15", "wire", "gh_edit", {"ops": f"## Tree demo #E6E6A0\n{hd}\nml = Mass Addition (I={hd})"})
    else:
        code = TREE_PY.replace("d = ghdoc()", "d = Grasshopper.Instances.ActiveCanvas.Document").replace("print(handle(p), ", "print(str(p.InstanceGuid), ")
        t = gh_py(S, "15", "tree", "import Grasshopper\n" + code)
        pid = re.search(r"([0-9a-f]{8}-[0-9a-f-]{27})", t).group(1)
        if S.name == "mcneel":
            if "Mass Addition" not in V2.LEARNED:      # ambiguous name: the model looks up the GUID first
                S.call("15", "search", "g1_search_components", {"query": "Mass Addition", "limit": 5})
                V2.LEARNED["Mass Addition"] = V2.info_guid("Mass Addition", "Maths")
            t2 = S.call("15", "wire", "g1_apply_graph", {"sliders": [], "components": [{"Key": "ml", "Selector": V2.LEARNED["Mass Addition"], "X": 300, "Y": 900}], "wires": [], "solve": False})
            ml = V2.ids_from(t2, ["ml"])["ml"]
            S.call("15", "wire", "g1_connect_many", {"wires": [{"SrcId": pid, "Src": "0", "DstId": ml, "Dst": "I"}], "solve": True})
        else:
            S.call("15", "wire", "gh_mutate_graph", {"operations": [{"op": "create", "alias": "ml", "component_name": "Mass Addition"}, {"op": "connect", "source": pid, "target": "ml", "target_input_name": "I"}, {"op": "recompute"}]})
    return {"gh": gh_counts(["Mass Addition"])}


MESSY = None   # (path, key→guid) prepared once


def prepare_messy():
    """Not counted: the same scrambled 30-node canvas for everyone (from the v1 case 16)."""
    global MESSY
    ops = read(HERE, "scripts", "messy.gh.txt")
    t = nx.call("gh_edit", {"new": True, "layout": "none", "ops": ops}, 60, log=False)[0]["text"]
    ids = ids_line(t)
    nrun("import random, System\nrandom.seed(7)\nfor o in ghdoc().Objects:\n    o.Attributes.Pivot = System.Drawing.PointF(random.uniform(0, 1400), random.uniform(0, 900))\n    o.Attributes.ExpireLayout(); o.Attributes.PerformLayout()")
    os.makedirs(TMP, exist_ok=True)
    path = os.path.join(TMP, "messy.gh")
    nx.call("gh_edit", {"save": path}, 60, log=False)
    guids = {}
    tg = nrun("import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\nprint(';'.join(handle(o) + '=' + str(o.InstanceGuid) for o in d.Objects))")
    hmap = dict(x.split("=") for x in next(l for l in tg.splitlines() if "=" in l).split(";"))
    for k, h in ids.items():
        guids[k] = hmap[h]
    MESSY = (path, ids, guids)


GROUPS16 = [("Inputs", "#DBABFF", ["w", "h", "nu", "nv", "amp", "fq", "d", "o"]), ("Base surface", "#ABDBFF", ["pl", "rc", "bs"]),
            ("Panel grid", "#ABDBFF", ["dv", "iso", "ar", "lst", "pn"]), ("Wave scaling", "#FFD6AB", ["cp", "m1", "sn", "m2", "ab", "ad", "sc"]),
            ("Fins", "#FFD6AB", ["ed", "jn", "nv2", "ex", "cap"]), ("Display", "#B2FAB4", ["col", "pv"])]


def c16(S):
    if MESSY is None: prepare_messy()
    path, ids, guids = MESSY
    nx.call("gh_edit", {"open": path, "reload": True}, 60, log=False)     # everyone starts from the same messy canvas
    # handles may differ after re-opening: map keys to the current canvas through the instance guids
    tg = nrun("import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\nprint(';'.join(handle(o) + '=' + str(o.InstanceGuid) for o in d.Objects))")
    g2h = {v: k for k, v in dict(x.split("=") for x in next(l for l in tg.splitlines() if "=" in l).split(";")).items()}
    if S.name == "nextra":
        S.call("16", "read", "query", {"scope": "gh", "filter": "all"})
        ops = "\n".join(f"## {n} {c}\n" + "\n".join(g2h[guids[k]] for k in ks) for n, c, ks in GROUPS16) + "\n##"
        S.call("16", "regroup+tidy", "gh_edit", {"ops": ops, "layout": "tidy"})
    elif S.name == "mcneel":
        S.call("16", "read", "g1_get_canvas_graph", {"include_data": False})
        body = "\n".join(f"group('{n}', '{c}', {json.dumps([guids[k] for k in ks])})" for n, c, ks in GROUPS16) + "\ntidy()"
        S.call("16", "regroup+tidy", "run_python", {"script": PY.gh(PY.GROUP, PY.LAYOUT, body=body)})
    else:
        S.call("16", "read", "gh_get_canvas_state", {})
        # its gh_mutate_graph accepted a groups list but created none, so groups go through its Python tool
        body = "\n".join(f"group('{n}', '{c}', {json.dumps([guids[k] for k in ks])})" for n, c, ks in GROUPS16)
        S.call("16", "regroup", "execute_rhinoscript_python_code", {"code": PY.gh(PY.GROUP, body=body)})
        S.call("16", "tidy", "gh_layout_components", {"include_groups": True})
    return {"gh": gh_counts(["Cap Holes"]), "overlaps": overlaps()}


def overlaps():
    t = nrun("import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\nobjs = [o for o in d.Objects if o.GetType().Name != 'GH_Group']\n"
             "n = 0\nfor i in range(len(objs)):\n    for j in range(i + 1, len(objs)):\n        a, b = objs[i].Attributes.Bounds, objs[j].Attributes.Bounds\n"
             "        if a.IntersectsWith(b): n += 1\nprint('OVERLAPS', n)")
    return next((l for l in t.splitlines() if l.startswith("OVERLAPS")), t[:100])


CANOPY = read(V3S, "canopy.py")


def c17(S):
    reset_rhino()
    t = rhino_code(S, "17", "build", CANOPY, {}, save_as="canopy")
    if S.name == "nextra":
        S.call("17", "inspect", "query", {"filter": "name:CANOPY", "level": "detail"})
        undo(S, "17", t)
        S.call("17", "raise_crown", "run", {"lib": "canopy", "args": {"h_mid": 8400}})
    else:
        py_call(S, "17", "inspect", "o = [x for x in doc.Objects if x.Attributes.Name == 'CANOPY'][0]\nb = o.Geometry\n"
                                    "print(b.Faces.Count, 'faces', 'area', round(rg.AreaMassProperties.Compute(b).Area / 1e6, 1), 'm2', 'volume', round(rg.VolumeMassProperties.Compute(b).Volume / 1e9, 2), 'm3', b.GetBoundingBox(True))")
        py_call(S, "17", "raise_crown", "for o in list(doc.Objects):\n    if doc.Layers[o.Attributes.LayerIndex].Name in ('Canopy', 'Canopy Columns'): doc.Objects.Delete(o, True)\n"
                                        "args = {'h_mid': 8400}\n" + CANOPY)
    return {"model": counts(), "crown": nrun("o = [x for x in doc.Objects if x.Attributes.Name == 'CANOPY']\nprint('TOP', round(o[0].Geometry.GetBoundingBox(True).Max.Z) if o else None, 'n', len(o))").split("TOP")[-1].strip()[:30]}


def c18(S):
    new_canvas()
    ids = gh_build(S, SP.TREES, "18")
    before = gh_counts(["Interpolate", "Loft"])
    if S.name == "nextra":
        S.call("18", "swap_graft", "gh_edit", {"ops": f"{ids['pt']} (X={ids['sx']}^g, Y={ids['sy']}^x)"})
    else:
        gh_py(S, "18", "swap_graft", PY.gh(PY.FLAGS, body=f"flag('{ids['pt']}', 'X', 'g')\nflag('{ids['pt']}', 'Y', 'x')"))
    return {"before": before, "after": gh_counts(["Interpolate", "Loft"])}


BIG = read(V3S, "big_model.py")


def c19(S):
    reset_rhino()
    nrun(BIG, timeout=300)                       # same 1,224-object model for everyone (not counted)
    if S.name == "nextra":
        S.call("19", "overview", "query", {})
        S.call("19", "longest_beam", "run", {"code": "b = max(q('layer:Beams name:*_L4'), key=lambda o: obb(o)['L'])\nprint(b.Attributes.Name, round(obb(b)['L']))"})
        S.call("19", "region_count", "query", {"filter": "layer:MEP inside:-1,-1,0,12001,30001,7300", "level": "summary"})
        S.call("19", "region_image", "view", {"view": "top", "focus": "layer:Columns", "tags": "off"})
    else:
        if S.name == "mcneel":
            py_call(S, "19", "overview", "from collections import Counter\nc = Counter((doc.Layers[o.Attributes.LayerIndex].Name, o.ObjectType.ToString()) for o in doc.Objects)\nprint(len(list(doc.Objects)), dict(c))")
        else:
            S.call("19", "overview", "get_document_summary", {})
        py_call(S, "19", "longest_beam", "best = None\nfor o in doc.Objects:\n    n = o.Attributes.Name or ''\n    if n.startswith('B') and n.endswith('_L4'):\n"
                                         "        bb = o.Geometry.GetBoundingBox(True); L = max(bb.Max.X - bb.Min.X, bb.Max.Y - bb.Min.Y)\n        if best is None or L > best[1]: best = (n, L)\nprint(best)")
        py_call(S, "19", "region_count", "box = rg.BoundingBox(rg.Point3d(-1, -1, 0), rg.Point3d(12001, 30001, 7300))\n"
                                         "print(sum(1 for o in doc.Objects if doc.Layers[o.Attributes.LayerIndex].Name == 'MEP' and box.Contains(o.Geometry.GetBoundingBox(True), True)))")
        if S.name == "mcneel":
            S.call("19", "region_image", "zoom_to_layer", {"layer": "Columns"})
            S.call("19", "region_image", "get_viewport_image", {"view": "top", "displayMode": "Shaded", "width": 800, "height": 600})
        else:
            S.call("19", "region_image", "capture_viewport", {"viewport": "top", "width": 800, "height": 600, "zoom_to_fit": True})
    return {"answers": "BX24_L4 12000 / 60 MEP"}


def c20(S):
    new_canvas()
    ids = gh_build(S, SP.KSHELL, "20")
    d20 = gh_value("Analyse", "MaxDisp")
    if S.name == "nextra":
        S.call("20", "thicker", "gh_edit", {"ops": f"{ids['th']} {{v=30}}"})
    elif S.name == "mcneel":
        gh_py(S, "20", "thicker", PY.gh(body=f"o('{ids['th']}').SetSliderValue(System.Decimal(30))"))
    else:
        S.call("20", "thicker", "gh_set_parameter_value", {"nickname": "Thickness cm", "value": 30})
    return {"disp_20cm": d20, "disp_30cm": gh_value("Analyse", "MaxDisp")}


CASES = {"01": c01, "02": c02, "03": c03, "05": c05, "06": c06, "07": c07, "08": c08, "09": c09, "10": c10,
         "12": c12, "13": c13, "14": c14, "15": c15, "16": c16, "17": c17, "18": c18, "19": c19, "20": c20}
CTX = {}


def main(which):
    global FRAME, RADIO
    sys.stdout.reconfigure(encoding="utf-8")
    FRAME = read(RS, "frame_building.py")
    RADIO = read(RS, "radio_component.py")
    os.makedirs(TMP, exist_ok=True)
    rm = V2.RhinoMcp()
    systems = [V2.Nextra(), V2.McNeel(), rm]
    fixed = V2.fixed_context(rm)
    checks = {}
    try:
        systems[0].call("00", "session", "memory", {"action": "list"})
        for cid in which:
            for S in systems:
                print(f"\n##### case {cid} · {S.name}")
                t0 = time.time()
                try:
                    checks[f"{cid}/{S.name}"] = CASES[cid](S)
                except Exception as e:
                    import traceback
                    checks[f"{cid}/{S.name}"] = {"harness_error": repr(e)[:300]}
                    traceback.print_exc()
                print("  check:", checks[f"{cid}/{S.name}"], f"({time.time() - t0:.0f}s)")
    finally:
        rm.close()
        with open(os.path.join(HERE, "rows_v3.json"), "w", encoding="utf-8") as f:
            json.dump({"fixed": fixed, "rows": ROWS, "checks": checks}, f, indent=1)


if __name__ == "__main__":
    main(sys.argv[1:] or list(CASES))
