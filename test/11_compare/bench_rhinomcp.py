"""Live benchmark: RhinoMCP (jingcheng-chen, uvx rhinomcp + Rhino plug-in on 127.0.0.1:1999), same tasks as Nextra/McNeel."""
import json, os, sys, subprocess, threading, queue, base64, io, time
OUT = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(OUT, "..", "..", "tools"))
from mcp_http import img_tokens

proc = subprocess.Popen(["uvx", "rhinomcp@latest"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, text=True, encoding="utf-8", bufsize=1)
q = queue.Queue()
threading.Thread(target=lambda: [q.put(l) for l in proc.stdout], daemon=True).start()
nid = [0]


def rpc(method, params=None, notify=False):
    msg = {"jsonrpc": "2.0", "method": method}
    if params is not None: msg["params"] = params
    if not notify: nid[0] += 1; msg["id"] = nid[0]
    proc.stdin.write(json.dumps(msg) + "\n"); proc.stdin.flush()
    if notify: return None
    while True:
        m = json.loads(q.get(timeout=300))
        if m.get("id") == msg["id"]: return m


rpc("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "bench", "version": "1"}})
rpc("notifications/initialized", notify=True)
rows = []


def call(task, tool, args):
    t0 = time.time()
    res = rpc("tools/call", {"name": tool, "arguments": args})
    content = (res or {}).get("result", {}).get("content", []) or [{"type": "text", "text": json.dumps(res)[:4000]}]
    text, tok = "", 0
    for c in content:
        if c.get("type") == "text": text += c["text"]; tok += len(c["text"]) // 4
        elif c.get("type") == "image": t, size = img_tokens(c["data"]); tok += t; text += f"\n[image {size}]"
    rows.append({"task": task, "system": "rhinomcp", "tool": tool, "in": len(json.dumps(args)) // 4, "out": tok, "sec": round(time.time() - t0, 2)})
    open(os.path.join(OUT, "out", f"{task}_rhinomcp_{tool}.txt"), "w", encoding="utf-8").write(json.dumps(args)[:4000] + "\n---\n" + text)
    return text


frame = open(os.path.join(OUT, "..", "_tmp", "frame.py"), encoding="utf-8").read()
prelude = "import Rhino, System, scriptcontext as sc\nimport Rhino.Geometry as rg\ndoc = sc.doc\n"

call("T1_overview", "get_document_summary", {})
call("T2_inspect", "get_object_info", {"name": "C_8_3_L2"})
call("T3_view", "capture_viewport", {"viewport": "perspective", "width": 800, "height": 450})
call("T4_build", "execute_rhinoscript_python_code", {"code": prelude + "args = {'storeys': 3, 'z0': 80}\n" + frame})
call("T4_build", "get_objects", {"layer_filter": "Columns", "limit": 50})          # what was made? (no delta in the reply)
call("T5_add_storey", "execute_rhinoscript_python_code", {"code": prelude + "args = {'storeys': 1, 'z0': 100}\n" + frame})

# GH: twisting tower in one gh_build_graph, then read back
comps = [
    {"alias": "n", "component_name": "Number Slider", "value": 12, "min": 2, "max": 40, "decimals": 0},
    {"alias": "fh", "component_name": "Number Slider", "value": 3.5, "min": 2.5, "max": 5},
    {"alias": "w", "component_name": "Number Slider", "value": 20, "min": 5, "max": 40},
    {"alias": "tw", "component_name": "Number Slider", "value": 45, "min": 0, "max": 180, "decimals": 0},
    {"alias": "ser", "component_name": "Series"}, {"alias": "pt", "component_name": "Construct Point"},
    {"alias": "pl", "component_name": "XY Plane"}, {"alias": "rec", "component_guid": "d93100b6-d50b-40b2-831a-814659dc38e3"},
    {"alias": "one", "component_name": "Subtraction"}, {"alias": "ang", "component_name": "Range"},
    {"alias": "mul", "component_name": "Multiplication"}, {"alias": "rad", "component_name": "Radians"},
    {"alias": "rot", "component_guid": "b7798b74-037e-4f0c-8ac7-dc1043d093e0"}, {"alias": "lft", "component_name": "Loft"},
    {"alias": "cap", "component_name": "Cap Holes"}]
C = lambda s, t, ti, so=0: {"source": s, "target": t, "target_input_index": ti, "source_output_index": so}
conns = [C("fh", "ser", 1), C("n", "ser", 2), C("ser", "pt", 2), C("pt", "pl", 0), C("pl", "rec", 0), C("w", "rec", 1), C("w", "rec", 2),
         C("n", "one", 0), C("one", "ang", 1), C("ang", "mul", 0), C("tw", "mul", 1), C("mul", "rad", 0),
         C("rec", "rot", 0), C("rad", "rot", 1), C("pl", "rot", 2), C("rot", "lft", 0), C("lft", "cap", 0)]
values = [{"target": "one", "input_index": 1, "value": 1}]
call("G1_build", "gh_build_graph", {"components": comps, "connections": conns, "values": values, "layout": {"enabled": True, "max_columns": 6}})
call("G3_readback", "gh_get_canvas_state", {})

json.dump(rows, open(os.path.join(OUT, "rows_rhinomcp.json"), "w"), indent=1)
for r in rows: print(r)
proc.kill()
