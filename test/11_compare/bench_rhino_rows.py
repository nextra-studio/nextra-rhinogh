"""Live benchmark, Rhino tasks: Nextra vs McNeel Rhino-MCP-Platform on the same open document.
Each task = the minimal call sequence an LLM needs on that system. Saves every request/response."""
import json, os, sys, subprocess
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
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


os.makedirs(os.path.join(OUT, 'out'), exist_ok=True)
