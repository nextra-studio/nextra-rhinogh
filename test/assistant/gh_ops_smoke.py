"""Smoke test of the Grasshopper functions of the engine's `ops` menu on a live Rhino (new definition each run).
usage: python test/assistant/gh_ops_smoke.py"""
import json, os, re, sys
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
import nx  # noqa: E402

fails = []


def nxc(tool, args):
    return nx.call(tool, args, log=False)[0]


def call(fn, args=None, targets=None, expect=None):
    res = nxc("ops", {"action": "run", "fn": fn, "args": args or {}, "targets": targets or []})
    text = res.get("text", "")
    good = res.get("ok") and (expect is None or re.search(expect, text))
    print(("PASS " if good else "FAIL ") + fn + ": " + text.replace("\n", " | ")[:170])
    if not good:
        fails.append(fn)
    return text


def menu():
    return json.loads(nxc("ops", {"action": "list"})["text"])


def slot(m, fn, name):
    f = next(x for x in m["functions"] if x["name"] == fn)
    return next(s for s in f["slots"] if s["name"] == name)


call("gh_new", expect=r"new|definition|empty")
m = menu()
comp = slot(m, "gh_add_component", "component")
print(f"catalogue: {len(comp['values'])} components in {len({p[0] for p in comp['paths'].values()})} categories")
call("gh_add_component", {"component": "Number Slider", "nick": "Radius"}, expect=r"g\d+")
call("gh_add_component", {"component": "Circle"}, expect=r"g\d+")
call("gh_add_component", {"component": "Divide Curve"}, expect=r"g\d+")
m = menu()
outs, ins = slot(m, "gh_connect", "from")["values"], slot(m, "gh_connect", "to")["values"]
print("outputs:", outs[:6], "\ninputs:", ins[:6])
src = next(o for o in outs if "Radius" in o)
rad = next(i for i in ins if "Circle" in i and "Radius" in i)
call("gh_connect", {"from": src, "to": rad}, expect=r"wire|ok|>|g\d")
circ_out = next(o for o in outs if o.split()[1] == "Circle" or "Circle:" in o)
div_in = next(i for i in ins if "Divide Curve" in i and ": Curve" in i)
call("gh_connect", {"from": circ_out, "to": div_in})
gh = json.loads(nxc("ops", {"action": "elements"})["text"])["gh"]
hs = [g["h"] for g in gh if g["kind"] == "component"]
call("gh_group", {"name": "Geometry", "colour": "geometry (orange)"}, hs, expect=r"group \"Geometry\"")
call("gh_select", {}, hs[:1], expect=r"selected 1")
call("gh_tidy", expect=r".")
call("gh_preview", {"show": False}, hs[:1])
call("gh_enable", {"enabled": True}, hs[:1])
q = nxc("query", {"scope": "gh"})["text"]
print(q[:400])
call("gh_disconnect", {"to": rad})
call("gh_delete", {}, hs[-1:])
print(f"\n{'ALL PASSED' if not fails else 'FAILED: ' + ', '.join(fails)}")
sys.exit(1 if fails else 0)
