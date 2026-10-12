"""Benchmark v2: Nextra vs McNeel Rhino-MCP-Platform vs RhinoMCP, live, same Rhino, same tasks.

Per call we record
  out  = tokens the model WRITES (the tool-call arguments: code, shorthand, graph JSON)
  in   = tokens the model READS back (tool result text + images)
Per system we add the fixed context the model must hold: tool schemas + server instructions (+ the Nextra skill).
Token estimate: text chars/4, image w*h/750 (Anthropic's image formula), same for all systems.
Correctness of every task is checked afterwards with neutral queries (not counted).

usage: python bench_v2.py [A] [B] [C]       (scenarios; default all)
"""
import base64, io, json, os, queue, subprocess, sys, threading, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools")); sys.path.insert(0, HERE)
import nx  # noqa: E402
import ghspec  # noqa: E402
from mcp_http import Client as HttpClient  # noqa: E402

OUT = os.path.join(HERE, "out")
ROWS = []


def tok(s):
    return len(s) // 4


def img_tok_b64(b64):
    try:
        from PIL import Image
        w, h = Image.open(io.BytesIO(base64.b64decode(b64))).size
        return w * h // 750
    except Exception:
        return 0


def img_tok_path(p):
    try:
        from PIL import Image
        w, h = Image.open(p).size
        return w * h // 750
    except Exception:
        return 0


def record(system, scen, task, tool, args, text, itok, ok=True):
    out_t = tok(json.dumps(args, ensure_ascii=False))
    in_t = tok(text) + itok
    n = sum(1 for r in ROWS if r["system"] == system and r["scenario"] == scen) + 1
    ROWS.append({"system": system, "scenario": scen, "task": task, "n": n, "tool": tool, "out": out_t, "in": in_t, "ok": ok})
    d = os.path.join(OUT, scen, system)
    if n == 1 and os.path.isdir(d):
        for f in os.listdir(d):   # this run's files only: no stale numbering from earlier runs (folder kept: OneDrive locks it)
            try: os.remove(os.path.join(d, f))
            except OSError: pass
    os.makedirs(d, exist_ok=True)
    with open(os.path.join(d, f"{n:02d}_{task}_{tool}.txt"), "w", encoding="utf-8") as f:
        f.write(json.dumps(args, ensure_ascii=False, indent=1)[:20000] + "\n---\n" + text)
    print(f"  {system:9s} {task:18s} {tool:34s} out {out_t:6d}  in {in_t:6d}")
    return text


# ----------------------------------------------------------------- clients

class Nextra:
    name = "nextra"

    def call(self, scen, task, tool, args, timeout=180):
        res, _, _, _ = nx.call(tool, args, timeout, log=False)
        text = res.get("text", "")
        return record(self.name, scen, task, tool, args, text, sum(img_tok_path(p) for p in res.get("images", [])), res.get("ok", True))


class McNeel:
    name = "mcneel"

    def __init__(self):
        self.c = HttpClient("http://localhost:5123/")

    def call(self, scen, task, tool, args, timeout=180):
        res = self.c.req("tools/call", {"name": tool, "arguments": args})
        text, it = "", 0
        for c in (res or {}).get("result", {}).get("content", []) or []:
            if c.get("type") == "text": text += c["text"]
            elif c.get("type") == "image": it += img_tok_b64(c["data"]); text += "\n[image]"
        if not text and res and "error" in res: text = json.dumps(res["error"])
        return record(self.name, scen, task, tool, args, text, it, not (res or {}).get("result", {}).get("isError", False))


class RhinoMcp:
    name = "rhinomcp"

    def __init__(self):
        self.p = subprocess.Popen(["uvx", "rhinomcp@0.4.1"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                  text=True, encoding="utf-8", bufsize=1)
        self.q = queue.Queue()
        threading.Thread(target=lambda: [self.q.put(l) for l in self.p.stdout], daemon=True).start()
        self.id = 0
        self.init = self.rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "bench", "version": "2"}})
        self.rpc("notifications/initialized", notify=True)

    def rpc(self, method, params=None, notify=False):
        m = {"jsonrpc": "2.0", "method": method}
        if params is not None: m["params"] = params
        if not notify: self.id += 1; m["id"] = self.id
        self.p.stdin.write(json.dumps(m) + "\n"); self.p.stdin.flush()
        if notify: return None
        while True:
            r = json.loads(self.q.get(timeout=300))
            if r.get("id") == m["id"]: return r

    def call(self, scen, task, tool, args, timeout=180):
        res = self.rpc("tools/call", {"name": tool, "arguments": args})
        text, it = "", 0
        for c in (res or {}).get("result", {}).get("content", []) or []:
            if c.get("type") == "text": text += c["text"]
            elif c.get("type") == "image": it += img_tok_b64(c["data"]); text += "\n[image]"
        if not text: text = json.dumps(res)[:4000]
        return record(self.name, scen, task, tool, args, text, it, not (res or {}).get("result", {}).get("isError", False))

    def close(self):
        try: self.p.kill()
        except Exception: pass


# ----------------------------------------------------------------- fixed context per system

def fixed_context(rm):
    node = "node"
    msgs = [{"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18"}}, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}]
    p = subprocess.run([node, os.path.join(REPO, "bridge", "server", "index.js")], input="\n".join(json.dumps(m) for m in msgs) + "\n",
                       capture_output=True, text=True, encoding="utf-8", timeout=60)
    r = [json.loads(l) for l in p.stdout.splitlines() if l.strip()]
    nx_schema = tok(json.dumps(r[1]["result"]["tools"])); nx_instr = tok(r[0]["result"].get("instructions", ""))
    skill = tok(open(os.path.join(REPO, "skill", "nextra", "SKILL.md"), encoding="utf-8").read())
    mc = HttpClient("http://localhost:5123/")
    mc_init = mc.req("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "b", "version": "1"}})
    mc_schema = tok(json.dumps(mc.req("tools/list", {})["result"]["tools"]))
    mc_instr = tok((mc_init or {}).get("result", {}).get("instructions", "") or "")
    rm_schema = tok(json.dumps(rm.rpc("tools/list", {})["result"]["tools"]))
    rm_instr = tok(rm.init.get("result", {}).get("instructions", "") or "")
    return {
        "nextra": {"schema": nx_schema, "instructions": nx_instr, "skill": skill},
        "mcneel": {"schema": mc_schema, "instructions": mc_instr, "skill": 0},
        "rhinomcp": {"schema": rm_schema, "instructions": rm_instr, "skill": 0},
    }


# ----------------------------------------------------------------- helpers for the other systems

MC_PRE = "import Rhino, System\nimport Rhino.Geometry as rg\ndoc = __rhino_doc__\n"
RM_PRE = "import Rhino, System, scriptcontext as sc\nimport Rhino.Geometry as rg\ndoc = sc.doc\n"
SUMMARY = ("\nfrom collections import Counter\n"
           "print(dict(Counter(doc.Layers[o.Attributes.LayerIndex].Name for o in doc.Objects)))\n")


def code(name):
    with open(os.path.join(HERE, name), encoding="utf-8") as f:
        return f.read()


def reset_rhino():
    nx.call("run", {"code": "for o in list(doc.Objects): doc.Objects.Delete(o, True)\nfor i in range(doc.Layers.Count-1,-1,-1):\n    l = doc.Layers[i]\n    if not l.IsDeleted and l.Name != 'Default': doc.Layers.Purge(i, True)\ndoc.ModelUnitSystem = Rhino.UnitSystem.Millimeters"}, 60, log=False)


def check_rhino():
    """Neutral check (not counted): object counts per layer + core x position + south mullions."""
    t = nx.call("run", {"code": "from collections import Counter\nc = Counter(doc.Layers[o.Attributes.LayerIndex].Name for o in doc.Objects)\n"
                                 "core = [o for o in doc.Objects if doc.Layers[o.Attributes.LayerIndex].Name == 'Core']\n"
                                 "xs = sorted(set(round(o.Geometry.GetBoundingBox(True).Min.X) for o in core))\n"
                                 "print(dict(c), 'core x', xs[:2], 'MS', sum(1 for o in doc.Objects if (o.Attributes.Name or '').startswith('MS')))"}, 60, log=False)[0]
    return [l for l in t["text"].splitlines() if l.startswith("{")][0] if "{" in t["text"] else t["text"]


def resolve(spec):
    """key → {guid, name, ambiguous, in, out, in_types}; the input ports each node is wired with pick the right variant."""
    wires = spec["wires"] + spec.get("late", [])
    ports = {n["k"]: sorted({dp for s, sp, d, dp, fl in wires if d == n["k"]} | {p for d, p, v in spec.get("values", []) if d == n["k"]})
             for n in spec["nodes"]}
    comps = [[n["t"], n.get("cat"), ports[n["k"]]] for n in spec["nodes"] if not n.get("slider")]
    tmp = os.path.join(HERE, "_resolve.json")
    nx.call("run", {"code": code("resolve_comps.py"), "args": {"comps": comps, "out": tmp}}, 60, log=False)
    raw = json.load(open(tmp, encoding="utf-8"))
    return {n["k"]: raw[n["t"] + "|" + str(n.get("cat")) + "|" + ",".join(ports[n["k"]])] for n in spec["nodes"] if not n.get("slider")}


def check_gh(spec):
    k, port = spec["check"]
    t = nx.call("run", {"code": "import Grasshopper\nd = Grasshopper.Instances.ActiveCanvas.Document\n"
                                 "n = [o for o in d.Objects if hasattr(o, 'Params')]\n"
                                 "tot = {}\n"
                                 f"for o in n:\n    if o.Name == '{ghspec.by_key(spec)[k]['t']}':\n        tot[o.Name] = tot.get(o.Name, 0) + sum(p.VolatileDataCount for p in o.Params.Output)\n"
                                 "print('objects', d.ObjectCount, 'groups', sum(1 for o in d.Objects if o.GetType().Name == 'GH_Group'), 'sink', tot, 'errors', sum(1 for o in d.Objects if hasattr(o,'RuntimeMessageLevel') and str(o.RuntimeMessageLevel) == 'Error'))"}, 60, log=False)[0]
    return [l for l in t["text"].splitlines() if l.startswith("objects")][0] if "objects" in t["text"] else t["text"][:200]


# ----------------------------------------------------------------- scenario A: detailed Rhino model + 3 edits + undo

def scenario_A(sysobj):
    S = sysobj.name
    reset_rhino()
    office = code("office.py")
    if S == "nextra":
        c = sysobj.call
        c("A", "0_session", "memory", {"action": "list"})
        c("A", "1_build", "run", {"code": office, "args": {}, "save_as": "office"})
        c("A", "2_look", "view", {"view": "iso"})
        c("A", "3_inspect_core", "query", {"filter": "layer:Core"})
        c("A", "4_add_storey", "run", {"lib": "office", "args": {"storeys": 1, "z0": 14400}})
        c("A", "5_move_core", "run", {"code": "xf = rg.Transform.Translation(3600, 0, 0)\nfor o in q('layer:Core'): doc.Objects.Transform(o, xf, True)"})
        t = c("A", "6_delete_south", "run", {"code": "for o in q('layer:Mullions name:MS*'): doc.Objects.Delete(o, True)"})
        res6 = check_rhino()
        ck = t.split("checkpoint ")[1].split()[0].strip(",") if "checkpoint " in t else None
        c("A", "7_undo", "undo", {"checkpoint": ck} if ck else {"steps": 1})
    elif S == "mcneel":
        c = sysobj.call
        c("A", "1_build", "run_python", {"script": MC_PRE + "args = {}\n" + office + SUMMARY})
        c("A", "2_look", "get_viewport_image", {"view": "perspective", "displayMode": "Shaded", "width": 800, "height": 600})
        c("A", "3_inspect_core", "list_objects", {"layer": "Core"})
        c("A", "4_add_storey", "run_python", {"script": MC_PRE + "args = {'storeys': 1, 'z0': 14400}\n" + office + SUMMARY})
        c("A", "5_move_core", "run_python", {"script": MC_PRE + "xf = rg.Transform.Translation(3600, 0, 0)\nn = 0\nfor o in doc.Objects.FindByLayer('Core'):\n    doc.Objects.Transform(o, xf, True); n += 1\nprint('moved', n)"})
        c("A", "6_delete_south", "run_python", {"script": MC_PRE + "n = 0\nfor o in doc.Objects.FindByLayer('Mullions'):\n    if (o.Attributes.Name or '').startswith('MS'):\n        doc.Objects.Delete(o, True); n += 1\nprint('deleted', n)"})
        res6 = check_rhino()
        c("A", "7_undo", "run_command", {"command": "_Undo"})
    else:
        c = sysobj.call
        c("A", "1_build", "execute_rhinoscript_python_code", {"code": RM_PRE + "args = {}\n" + office + SUMMARY})
        c("A", "2_look", "capture_viewport", {"viewport": "perspective", "width": 800, "height": 600})
        c("A", "3_inspect_core", "get_objects", {"layer_filter": "Core"})
        c("A", "4_add_storey", "execute_rhinoscript_python_code", {"code": RM_PRE + "args = {'storeys': 1, 'z0': 14400}\n" + office + SUMMARY})
        c("A", "5_move_core", "execute_rhinoscript_python_code", {"code": RM_PRE + "xf = rg.Transform.Translation(3600, 0, 0)\nn = 0\nfor o in doc.Objects.FindByLayer('Core'):\n    doc.Objects.Transform(o, xf, True); n += 1\nprint('moved', n)"})
        c("A", "6_delete_south", "execute_rhinoscript_python_code", {"code": RM_PRE + "n = 0\nfor o in doc.Objects.FindByLayer('Mullions'):\n    if (o.Attributes.Name or '').startswith('MS'):\n        doc.Objects.Delete(o, True); n += 1\nprint('deleted', n)"})
        res6 = check_rhino()
        c("A", "7_undo", "undo", {"steps": 1})
    return {"after_edits": res6, "after_undo": check_rhino()}


# ----------------------------------------------------------------- scenarios B / C: Grasshopper build + edits

def new_canvas():
    nx.call("gh_edit", {"new": True}, 60, log=False)


def ids_from(text, keys):
    """Pull key → instance-id maps out of the other systems' replies (the model reads them there too)."""
    try:
        j = json.loads(text)
    except Exception:
        j = None
    res = {}
    def walk(x):
        if isinstance(x, dict):
            k = x.get("Key") or x.get("alias") or x.get("key")
            v = x.get("Id") or x.get("instance_id") or x.get("instanceId") or x.get("id") or x.get("guid")
            if k in keys and isinstance(v, str): res[k] = v
            for kk, vv in x.items():
                if kk in keys and isinstance(vv, str) and len(vv) >= 32: res[kk] = vv
                walk(vv)
        elif isinstance(x, list):
            for y in x: walk(y)
    walk(j)
    return res


def gh_scenario(sysobj, spec, scen, edits):
    S = sysobj.name
    info = resolve(spec)
    keys = [n["k"] for n in spec["nodes"]]
    ambiguous = sorted({ghspec.by_key(spec)[k]["t"] for k, i in info.items() if i and i["ambiguous"]})
    new_canvas()
    c = sysobj.call
    if S == "nextra":
        if scen == "B": c(scen, "0_docs", "query", {"scope": "docs", "filter": "shorthand"})     # read the grammar once per session
        t = c(scen, "1_build", "gh_edit", {"ops": ghspec.shorthand(spec)})
        ids = dict(l.split("=") for l in next(x for x in t.splitlines() if x.startswith("ids:"))[4:].split())
        built = check_gh(spec)
        for name, fn in edits:
            fn(sysobj, scen, name, ids, info)
        c(scen, "9_look", "view", {"target": "gh"})
    elif S == "mcneel":
        for a in ambiguous:
            c(scen, "1_search", "g1_search_components", {"query": a, "limit": 5})
        graph = ghspec.mcneel_graph(spec, info)
        t = c(scen, "1_build", "g1_apply_graph", graph)
        ids = ids_from(t, keys)
        ids.update(mcneel_recover(sysobj, scen, spec, graph, t, ids))
        c(scen, "1_fix", "run_python", {"script": ghspec.fix_script(spec, ids)})
        c(scen, "1_verify", "g1_get_canvas_graph", {"include_data": True, "sample_size": 2})
        built = check_gh(spec)
        for name, fn in edits:
            fn(sysobj, scen, name, ids, info)
        c(scen, "9_look", "get_viewport_image", {"view": "perspective", "displayMode": "Shaded", "width": 800, "height": 600})
    else:
        for a in ambiguous:
            c(scen, "1_search", "gh_search_components", {"query": a, "limit": 5})
        g = ghspec.rhinomcp_graph(spec, info)
        t = c(scen, "1_build", "gh_build_graph", g)
        ids = ids_from(t, keys)
        flags = [w for w in spec["wires"] if w[4]]
        if flags:
            c(scen, "1_fix", "execute_rhinoscript_python_code", {"code": ghspec.fix_script(spec, ids, need_values=False, need_groups=False)})
        # no separate verify call: gh_build_graph's reply already lists every component's outputs and messages
        built = check_gh(spec)
        for name, fn in edits:
            fn(sysobj, scen, name, ids, info)
        c(scen, "9_look", "gh_capture_preview", {})
    return {"built": built, "after_edits": check_gh(spec), "ambiguous": ambiguous}


LEARNED = {}   # component name → GUID the model learned from McNeel's ambiguity errors (reused in later edits)


def mcneel_recover(s, scen, spec, graph, reply, ids):
    """What the model must do after g1_apply_graph rejects ambiguous names: place those components by the GUID
    listed in the error (the candidate in the right category), then rewire the wires that failed, by instance id."""
    import re
    try:
        j = json.loads(reply)
    except Exception:
        return {}
    errs = j.get("PlaceErrors") or []
    if not errs:
        return {}
    nodes = ghspec.by_key(spec)
    comps = []
    for e in errs:
        k = e["Key"]
        cands = re.findall(r"([0-9a-f\-]{36}) \(([^)]*)\)", e.get("Error", ""))
        cat = nodes[k].get("cat") or ""
        pick = next((g for g, where in cands if where.startswith(cat)), cands[0][0] if cands else None)
        if pick is None:
            continue
        LEARNED[nodes[k]["t"]] = pick
        x = next((c for c in graph["components"] if c["Key"] == k), {"X": 0, "Y": 0})
        comps.append({"Key": k, "Selector": pick, "X": x["X"], "Y": x["Y"]})
    t2 = s.call(scen, "1_recover", "g1_apply_graph", {"sliders": [], "components": comps, "wires": [], "solve": False})
    new = ids_from(t2, [c["Key"] for c in comps])
    allids = dict(ids); allids.update(new)
    failed = [graph["wires"][w["Index"]] for w in j.get("Wires", []) if not w.get("Ok")]
    wires = [{"SrcId": allids[w["SrcKey"]], "Src": w["Src"], "DstId": allids[w["DstKey"]], "Dst": w["Dst"]} for w in failed if w["SrcKey"] in allids and w["DstKey"] in allids]
    if wires:
        s.call(scen, "1_recover", "g1_connect_many", {"wires": wires, "solve": True})
    return new


def mc_sel(name, cat):
    """Selector the model would now use for a component name: the learned GUID if McNeel called it ambiguous."""
    return LEARNED.get(name, name)


# facade edits: (3) attractor point → line, (4) extra slider scaling the openings, (5) tidy

def facade_edit_curve(s, scen, name, ids, info):
    if s.name == "nextra":
        s.call(scen, name, "gh_edit", {"ops": f"- {ids['ap']} {ids['ds']}\n## Attractor\np1 = Construct Point (X=0, Z={ids['Az']})\np2 = Construct Point (X={ids['W']}, Z={ids['H']})\nln = Line (A=p1, B=p2)\ncc = Curve Closest Point (P={ids['ar']}.C, C=ln)\n{ids['bnd']} (N=cc.D)\n{ids['rm']} (V=cc.D)"})
    elif s.name == "mcneel":
        g = {"sliders": [], "components": [{"Key": "p1", "Selector": mc_sel("Construct Point", "Vector"), "X": 600, "Y": 500}, {"Key": "p2", "Selector": mc_sel("Construct Point", "Vector"), "X": 600, "Y": 570},
                                           {"Key": "ln", "Selector": info_guid("Line", "Curve"), "X": 770, "Y": 530}, {"Key": "cc", "Selector": mc_sel("Curve Closest Point", "Curve"), "X": 940, "Y": 530}],
             "wires": [{"SrcKey": "p1", "Src": "Pt", "DstKey": "ln", "Dst": "A"}, {"SrcKey": "p2", "Src": "Pt", "DstKey": "ln", "Dst": "B"}, {"SrcKey": "ln", "Src": "L", "DstKey": "cc", "Dst": "C"}], "solve": False}
        t = s.call(scen, name, "g1_apply_graph", g)
        new = ids_from(t, ["p1", "p2", "ln", "cc"])
        W = lambda si, sp, di, dp: {"SrcId": si, "Src": sp, "DstId": di, "Dst": dp}
        s.call(scen, name, "g1_connect_many", {"wires": [W(ids["Az"], "0", new["p1"], "Z"), W(ids["W"], "0", new["p2"], "X"), W(ids["H"], "0", new["p2"], "Z"),
                                                         W(ids["ar"], "C", new["cc"], "P"), W(new["cc"], "D", ids["bnd"], "N"), W(new["cc"], "D", ids["rm"], "V")], "solve": False})
        s.call(scen, name, "run_python", {"script": "import Grasshopper, System\ndoc = Grasshopper.Instances.ActiveCanvas.Document\n"
                                                    f"for k in ['{ids['ap']}', '{ids['ds']}']:\n    doc.RemoveObject(doc.FindObject(System.Guid(k), True), False)\ndoc.NewSolution(False)\nprint('ok')"})
    else:
        s.call(scen, name, "gh_mutate_graph", {"operations": [
            {"op": "delete", "target": "ap"}, {"op": "delete", "target": "ds"},
            {"op": "create", "alias": "p1", "component_name": "Construct Point"}, {"op": "create", "alias": "p2", "component_name": "Construct Point"},
            {"op": "create", "alias": "ln", "component_guid": info_guid("Line", "Curve")}, {"op": "create", "alias": "cc", "component_name": "Curve Closest Point"},
            {"op": "connect", "source": "Az", "target": "p1", "target_input_name": "Z"}, {"op": "connect", "source": "W", "target": "p2", "target_input_name": "X"},
            {"op": "connect", "source": "H", "target": "p2", "target_input_name": "Z"}, {"op": "connect", "source": "p1", "target": "ln", "target_input_name": "A"},
            {"op": "connect", "source": "p2", "target": "ln", "target_input_name": "B"}, {"op": "connect", "source": "ln", "target": "cc", "target_input_name": "C"},
            {"op": "connect", "source": "ar", "source_output_name": "C", "target": "cc", "target_input_name": "P"},
            {"op": "connect", "source": "cc", "source_output_name": "D", "target": "bnd", "target_input_name": "N"},
            {"op": "connect", "source": "cc", "source_output_name": "D", "target": "rm", "target_input_name": "V"},
            {"op": "recompute"}], "layout": {"enabled": True, "targets": ["p1", "p2", "ln", "cc", "bnd", "rm"], "max_columns": 4}})


def facade_edit_ratio(s, scen, name, ids, info):
    if s.name == "nextra":
        s.call(scen, name, "gh_edit", {"ops": f"## Inputs\nk = Number Slider {{v=1, r=0.2..1.5}} \"Open scale\"\n## Attractor\nmul = Multiplication (A={ids['rm']}.R, B=k)\n{ids['sc']} (F=mul)"})
    elif s.name == "mcneel":
        t = s.call(scen, name, "g1_apply_graph", {"sliders": [{"Key": "k", "Min": 0.2, "Value": 1, "Max": 1.5, "Type": "float", "Name": "Open scale", "X": 0, "Y": 700}],
                                                  "components": [{"Key": "mul", "Selector": info_guid("Multiplication", "Maths"), "X": 1100, "Y": 300}], "wires": [{"SrcKey": "k", "Src": "0", "DstKey": "mul", "Dst": "B"}], "solve": False})
        new = ids_from(t, ["k", "mul"])
        s.call(scen, name, "g1_connect_many", {"wires": [{"SrcId": ids["rm"], "Src": "R", "DstId": new["mul"], "Dst": "A"}, {"SrcId": new["mul"], "Src": "R", "DstId": ids["sc"], "Dst": "F"}], "solve": False})
        s.call(scen, name, "run_python", {"script": "import Grasshopper, System\ndoc = Grasshopper.Instances.ActiveCanvas.Document\n"
                                                    f"sc = doc.FindObject(System.Guid('{ids['sc']}'), True); f = [p for p in sc.Params.Input if p.NickName == 'F'][0]\n"
                                                    f"for src in list(f.Sources):\n    if src.Attributes.GetTopLevel.DocObject.InstanceGuid == System.Guid('{ids['rm']}'): f.RemoveSource(src)\n"
                                                    "doc.NewSolution(False)\nprint('ok')"})
    else:
        s.call(scen, name, "gh_mutate_graph", {"operations": [
            {"op": "create", "alias": "k", "component_name": "Number Slider", "value": 1, "min": 0.2, "max": 1.5, "nickname": "Open scale"},
            {"op": "create", "alias": "mul", "component_guid": info_guid("Multiplication", "Maths")},
            {"op": "disconnect", "source": "rm", "source_output_name": "R", "target": "sc", "target_input_name": "F"},
            {"op": "connect", "source": "rm", "source_output_name": "R", "target": "mul", "target_input_name": "A"},
            {"op": "connect", "source": "k", "target": "mul", "target_input_name": "B"},
            {"op": "connect", "source": "mul", "target": "sc", "target_input_name": "F"}, {"op": "recompute"}]})


TIDY_PY = """import Grasshopper, System
doc = Grasshopper.Instances.ActiveCanvas.Document
objs = [o for o in doc.Objects if o.GetType().Name != 'GH_Group']
rank = {}
def up(o):
    ps = o.Params.Input if hasattr(o, 'Params') else ([o] if hasattr(o, 'Sources') else [])
    return [s.Attributes.GetTopLevel.DocObject for p in ps for s in p.Sources]
for _ in range(len(objs)):
    for o in objs:
        rank[o.InstanceGuid] = 1 + max([rank.get(u.InstanceGuid, 0) for u in up(o)] or [-1])
cols = {}
for o in sorted(objs, key=lambda o: rank[o.InstanceGuid]):
    c = rank[o.InstanceGuid]; r = cols.get(c, 0); cols[c] = r + 1
    o.Attributes.Pivot = System.Drawing.PointF(c * 220, r * 90); o.Attributes.ExpireLayout()
for g in [o for o in doc.Objects if o.GetType().Name == 'GH_Group']: g.ExpireCaches()
Grasshopper.Instances.ActiveCanvas.Refresh()
print('ok')"""


def edit_tidy(s, scen, name, ids, info):
    if s.name == "nextra":
        s.call(scen, name, "gh_edit", {"layout": "tidy"})
    elif s.name == "mcneel":
        s.call(scen, name, "run_python", {"script": TIDY_PY})       # no layout tool: the model writes one
    else:
        s.call(scen, name, "gh_layout_components", {})


# gridshell edits: (3) amplitude + density change, (4) add edge beams

def grid_edit_params(s, scen, name, ids, info):
    if s.name == "nextra":
        s.call(scen, name, "gh_edit", {"ops": f"{ids['Am']} {{v=7}}\n{ids['Nu']} {{v=24}}"})
    elif s.name == "mcneel":
        s.call(scen, name, "run_python", {"script": "import Grasshopper, System\ndoc = Grasshopper.Instances.ActiveCanvas.Document\n"
                                                    f"doc.FindObject(System.Guid('{ids['Am']}'), True).SetSliderValue(System.Decimal(7))\n"
                                                    f"doc.FindObject(System.Guid('{ids['Nu']}'), True).SetSliderValue(System.Decimal(24))\ndoc.NewSolution(False)\nprint('ok')"})
    else:
        s.call(scen, name, "gh_set_parameter_value", {"nickname": "Wave", "value": 7})
        s.call(scen, name, "gh_set_parameter_value", {"nickname": "U", "value": 24})


def grid_edit_edges(s, scen, name, ids, info):
    if s.name == "nextra":
        s.call(scen, name, "gh_edit", {"ops": f"## Members\nbe = Brep Edges (B={ids['lf']})\nep = Pipe (C=be.En, R=0.25)"})
    elif s.name == "mcneel":
        t = s.call(scen, name, "g1_apply_graph", {"sliders": [], "components": [{"Key": "be", "Selector": info_guid("Brep Edges", "Surface"), "X": 1500, "Y": 400},
                                                                               {"Key": "ep", "Selector": mc_sel("Pipe", "Surface"), "X": 1700, "Y": 400}],
                                                  "wires": [{"SrcKey": "be", "Src": "En", "DstKey": "ep", "Dst": "C"}], "solve": False})
        new = ids_from(t, ["be", "ep"])
        s.call(scen, name, "g1_connect_many", {"wires": [{"SrcId": ids["lf"], "Src": "L", "DstId": new["be"], "Dst": "B"}], "solve": False})
        s.call(scen, name, "run_python", {"script": "import Grasshopper, System\nfrom Grasshopper.Kernel.Types import GH_Number\ndoc = Grasshopper.Instances.ActiveCanvas.Document\n"
                                                    f"r = [p for p in doc.FindObject(System.Guid('{new['ep']}'), True).Params.Input if p.NickName == 'R'][0]\n"
                                                    "r.PersistentData.Clear(); r.PersistentData.Append(GH_Number(0.25))\ndoc.NewSolution(False)\nprint('ok')"})
    else:
        t = s.call(scen, name, "gh_mutate_graph", {"operations": [
            {"op": "create", "alias": "be", "component_guid": info_guid("Brep Edges", "Surface")}, {"op": "create", "alias": "ep", "component_name": "Pipe"},
            {"op": "connect", "source": "lf", "target": "be", "target_input_name": "B"}, {"op": "connect", "source": "be", "source_output_name": "En", "target": "ep", "target_input_name": "C"},
            {"op": "recompute"}]})
        ep = ids_from(t, ["ep"]).get("ep")
        # mutate has no value operation: set the new pipe's radius with the separate value tool
        s.call(scen, name, "gh_set_parameter_value", {"instance_id": ep, "input_index": 1, "value": 0.25})


_GUIDS = {}


def info_guid(name, cat):
    key = (name, cat)
    if key not in _GUIDS:
        tmp = os.path.join(HERE, "_resolve1.json")
        nx.call("run", {"code": code("resolve_comps.py"), "args": {"comps": [[name, cat]], "out": tmp}}, 60, log=False)
        raw = json.load(open(tmp, encoding="utf-8"))
        _GUIDS[key] = list(raw.values())[0]["guid"]
    return _GUIDS[key]


# ----------------------------------------------------------------- main

def main(scens):
    sys.stdout.reconfigure(encoding="utf-8")
    os.makedirs(OUT, exist_ok=True)
    rm = RhinoMcp()
    systems = [Nextra(), McNeel(), rm]
    fixed = fixed_context(rm)
    checks = {}
    try:
        for S in systems:
            print(f"\n##### {S.name}")
            if "A" in scens:
                checks[f"A/{S.name}"] = scenario_A(S)
            if "B" in scens:
                checks[f"B/{S.name}"] = gh_scenario(S, ghspec.FACADE, "B", [("3_attractor_curve", facade_edit_curve), ("4_open_scale", facade_edit_ratio), ("5_tidy", edit_tidy)])
            if "C" in scens:
                checks[f"C/{S.name}"] = gh_scenario(S, ghspec.GRIDSHELL, "C", [("3_params", grid_edit_params), ("4_edge_beams", grid_edit_edges)])
    finally:
        rm.close()
        with open(os.path.join(HERE, "rows_v2.json"), "w", encoding="utf-8") as f:
            json.dump({"fixed": fixed, "rows": ROWS, "checks": checks}, f, indent=1)
    print(json.dumps(checks, indent=1))


if __name__ == "__main__":
    main(sys.argv[1:] or ["A", "B", "C"])
