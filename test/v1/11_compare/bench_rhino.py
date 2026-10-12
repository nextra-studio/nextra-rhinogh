"""Live benchmark, Rhino tasks: Nextra vs McNeel Rhino-MCP-Platform on the same open document.
Each task = the minimal call sequence an LLM needs on that system. Saves every request/response."""
import json, os, sys, subprocess
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools"))
import nx
from mcp_http import Client, img_tokens

OUT = os.path.dirname(os.path.abspath(__file__))
mc = Client("http://localhost:5123/")
rows = []


def nextra(task, tool, args):
    res, ti, to, dt = nx.call(tool, args, timeout=120, log=False)
    rows.append({"task": task, "system": "nextra", "tool": tool, "in": ti, "out": to, "sec": round(dt, 2)})
    open(os.path.join(OUT, "out", f"{task}_nextra_{tool}.txt"), "w", encoding="utf-8").write(json.dumps(args) + "\n---\n" + res.get("text", ""))
    return res


def mcneel(task, tool, args):
    res = mc.req("tools/call", {"name": tool, "arguments": args})
    content = (res or {}).get("result", {}).get("content", [])
    text, tok = "", 0
    for c in content:
        if c.get("type") == "text": text += c["text"]; tok += len(c["text"]) // 4
        elif c.get("type") == "image": t, size = img_tokens(c["data"]); tok += t; text += f"\n[image {size}]"
    rows.append({"task": task, "system": "mcneel", "tool": tool, "in": len(json.dumps(args)) // 4, "out": tok})
    open(os.path.join(OUT, "out", f"{task}_mcneel_{tool}.txt"), "w", encoding="utf-8").write(json.dumps(args)[:4000] + "\n---\n" + text)
    return text


os.makedirs(os.path.join(OUT, "out"), exist_ok=True)
frame_code = open(os.path.join(OUT, "frame.py"), encoding="utf-8").read()

# T1 overview
nextra("T1_overview", "query", {})
mcneel("T1_overview", "list_objects", {})
# T2 one object in depth (a column)
nextra("T2_inspect", "query", {"filter": "layer:ARCH::Columns", "level": "detail", "limit": 1})
mcneel("T2_inspect", "list_objects", {"layer": "ARCH::Columns", "limit": 1})
# T3 look at it
nextra("T3_view", "view", {"view": "iso"})
mcneel("T3_view", "get_viewport_image", {"view": "perspective", "displayMode": "Shaded", "width": 800, "height": 450})
# T4 build a 3-storey frame by code (same script on both; McNeel needs the doc handle name)
nextra("T4_build", "run", {"code": frame_code, "args": {"storeys": 3, "z0": 20}, "label": "frame"})
mc_code = "import Rhino, System\nimport Rhino.Geometry as rg\ndoc = __rhino_doc__\nargs = {'storeys': 3, 'z0': 40}\n" + frame_code
mcneel("T4_build", "run_python", {"script": mc_code})
# T5 add one storey: Nextra reuses the saved library script; McNeel resends the code
nextra("T5_add_storey", "run", {"lib": "frame_building", "args": {"storeys": 1, "z0": 60}})
mcneel("T5_add_storey", "run_python", {"script": mc_code.replace("'storeys': 3, 'z0': 40", "'storeys': 1, 'z0': 70")})

json.dump(rows, open(os.path.join(OUT, "rows_rhino.json"), "w"), indent=1)
for r in rows: print(r)
