"""Does McNeel's run_python + _Undo work, and does Nextra activity in between change that? (diagnostic, not counted)"""
import sys, os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools"))
from mcp_http import Client
import nx

mc = Client("http://localhost:5123/")


def m(tool, args):
    r = mc.req("tools/call", {"name": tool, "arguments": args})
    return "".join(c.get("text", "") for c in r["result"]["content"]).strip()[:100]


def cnt():
    return nx.call("query", {"filter": "name:UT*", "limit": 1}, 30, log=False)[0]["text"].splitlines()[0]


ADD = "import Rhino\ndoc = __rhino_doc__\nfor i in range(5):\n    a = Rhino.DocObjects.ObjectAttributes(); a.Name = 'UT%d' % i\n    doc.Objects.AddPoint(Rhino.Geometry.Point3d(i,0,0), a)\nprint('added')"
DEL = "doc = __rhino_doc__\nfor o in list(doc.Objects):\n    if (o.Attributes.Name or '').startswith('UT'): doc.Objects.Delete(o, True)\nprint('deleted')"


def trial(label, between=None):
    m("run_python", {"script": ADD})
    m("run_python", {"script": DEL})
    if between: between()
    u = m("run_command", {"command": "_Undo"})
    print(f"{label:45s} undo says: {u[:40]!r:45s} → {cnt()}")
    m("run_python", {"script": DEL})   # clean up


trial("1 fresh Rhino, McNeel only")
trial("2 + Nextra read-only query in between", lambda: nx.call("query", {}, 30, log=False))
trial("3 + Nextra no-change run in between", lambda: nx.call("run", {"code": "print(1)"}, 30, log=False))
trial("4 + Nextra run that changes the doc", lambda: nx.call("run", {"code": "doc.Objects.AddPoint(rg.Point3d(99,0,0))"}, 30, log=False))
nx.call("gh_edit", {"new": True, "ops": "a = Number Slider {v=1}"}, 30, log=False)
trial("5 after a Nextra gh_edit earlier")
