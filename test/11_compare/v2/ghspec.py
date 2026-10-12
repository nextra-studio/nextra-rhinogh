"""Neutral Grasshopper task specs + emitters for each system's own format.

The same graph (components, wires, literal inputs, flags, expressions, groups) is written as
 - Nextra shorthand (gh_edit ops)
 - McNeel g1_apply_graph JSON (+ a run_python fix for what the graph call cannot express)
 - RhinoMCP gh_build_graph JSON (+ an execute-python fix for flags/expressions/groups)
so every system carries identical content and only the format/protocol differs."""
import json

# node: k, t (component name), cat (category for disambiguation), sh_cat (emit @cat in shorthand),
#       slider {v,min,max,int}, swatch '#RRGGBB', nick
# wire: (src, src_port|None, dst, dst_port, flags)   value: (dst, port, literal)   expr: (dst, port, expr)

FACADE = {
    "name": "facade",
    "nodes": [
        dict(k="W", t="Number Slider", slider=dict(v=30, min=10, max=60), nick="Width"),
        dict(k="H", t="Number Slider", slider=dict(v=12, min=3, max=30), nick="Height"),
        dict(k="U", t="Number Slider", slider=dict(v=15, min=2, max=40, int=True), nick="U"),
        dict(k="V", t="Number Slider", slider=dict(v=6, min=2, max=20, int=True), nick="V"),
        dict(k="Ax", t="Number Slider", slider=dict(v=10, min=0, max=60), nick="Attr x"),
        dict(k="Az", t="Number Slider", slider=dict(v=6, min=0, max=30), nick="Attr z"),
        dict(k="Rmin", t="Number Slider", slider=dict(v=0.15, min=0.05, max=1), nick="Open min"),
        dict(k="Rmax", t="Number Slider", slider=dict(v=0.85, min=0.05, max=1), nick="Open max"),
        dict(k="D", t="Number Slider", slider=dict(v=0.3, min=0.05, max=2), nick="Depth"),
        dict(k="pl", t="XZ Plane", cat="Vector"),
        dict(k="rc", t="Rectangle", cat="Curve"),
        dict(k="bs", t="Boundary Surfaces", cat="Surface"),
        dict(k="dv", t="Divide Domain²", cat="Maths"),
        dict(k="iso", t="Isotrim", cat="Surface"),
        dict(k="ar", t="Area", cat="Surface"),
        dict(k="ap", t="Construct Point", cat="Vector"),
        dict(k="ds", t="Distance", cat="Vector"),
        dict(k="bnd", t="Bounds", cat="Maths"),
        dict(k="dom", t="Construct Domain", cat="Maths"),
        dict(k="rm", t="Remap Numbers", cat="Maths"),
        dict(k="sc", t="Scale", cat="Transform"),
        dict(k="ed", t="Brep Edges", cat="Surface"),
        dict(k="jn", t="Join Curves", cat="Curve"),
        dict(k="uy", t="Unit Y", cat="Vector"),
        dict(k="ex", t="Extrude", cat="Surface"),
        dict(k="cs", t="Colour Swatch", cat="Params", swatch="#5A8CB4"),
        dict(k="pv", t="Custom Preview", cat="Display"),
    ],
    "wires": [
        ("pl", None, "rc", "P", ""), ("W", None, "rc", "X", ""), ("H", None, "rc", "Y", ""),
        ("rc", "R", "bs", "E", ""), ("bs", None, "dv", "I", ""), ("U", None, "dv", "U", ""), ("V", None, "dv", "V", ""),
        ("bs", None, "iso", "S", ""), ("dv", None, "iso", "D", ""), ("iso", None, "ar", "G", ""),
        ("Ax", None, "ap", "X", ""), ("Az", None, "ap", "Z", ""),
        ("ar", "C", "ds", "A", ""), ("ap", None, "ds", "B", ""),
        ("ds", None, "bnd", "N", ""), ("Rmin", None, "dom", "A", ""), ("Rmax", None, "dom", "B", ""),
        ("ds", None, "rm", "V", ""), ("bnd", None, "rm", "S", ""), ("dom", None, "rm", "T", ""),
        ("iso", None, "sc", "G", ""), ("ar", "C", "sc", "C", ""), ("rm", "R", "sc", "F", ""),
        ("sc", "G", "ed", "B", ""), ("ed", "En", "jn", "C", ""), ("D", None, "uy", "F", ""),
        ("jn", None, "ex", "B", ""), ("uy", None, "ex", "D", ""), ("ex", None, "pv", "G", ""), ("cs", None, "pv", "M", ""),
    ],
    "values": [],
    "exprs": [],
    "groups": [
        ("Inputs", "#DBABFF", ["W", "H", "U", "V", "Ax", "Az", "Rmin", "Rmax", "D"]),
        ("Panels", "#ABDBFF", ["pl", "rc", "bs", "dv", "iso", "ar"]),
        ("Attractor", "#FFD6AB", ["ap", "ds", "bnd", "dom", "rm"]),
        ("Openings", "#FFD6AB", ["sc", "ed", "jn", "uy", "ex"]),
        ("Display", "#B2FAB4", ["cs", "pv"]),
    ],
    "check": ("ex", "E"),
}

GRIDSHELL = {
    "name": "gridshell",
    "nodes": [
        dict(k="L", t="Number Slider", slider=dict(v=36, min=10, max=80), nick="Length"),
        dict(k="Wd", t="Number Slider", slider=dict(v=24, min=6, max=60), nick="Width"),
        dict(k="Hc", t="Number Slider", slider=dict(v=6, min=1, max=20), nick="Crown"),
        dict(k="Am", t="Number Slider", slider=dict(v=4, min=0, max=12), nick="Wave"),
        dict(k="Nu", t="Number Slider", slider=dict(v=18, min=4, max=60, int=True), nick="U"),
        dict(k="Nv", t="Number Slider", slider=dict(v=12, min=4, max=40, int=True), nick="V"),
        dict(k="Rp", t="Number Slider", slider=dict(v=0.08, min=0.02, max=0.5), nick="Member r"),
        dict(k="dy", t="Construct Domain", cat="Maths"),
        dict(k="ys", t="Range", cat="Sets"),
        dict(k="yw", t="Division", cat="Maths"),
        dict(k="pi", t="Pi", cat="Maths"),
        dict(k="sn", t="Sine", cat="Maths"),
        dict(k="amp", t="Multiplication", cat="Maths"),
        dict(k="zc", t="Addition", cat="Maths"),
        dict(k="half", t="Division", cat="Maths"),
        dict(k="pa", t="Construct Point", cat="Vector"),
        dict(k="pb", t="Construct Point", cat="Vector"),
        dict(k="pc", t="Construct Point", cat="Vector"),
        dict(k="arc", t="Arc 3Pt", cat="Curve"),
        dict(k="lo", t="Loft Options", cat="Surface"),
        dict(k="lf", t="Loft", cat="Surface"),
        dict(k="sp", t="Divide Surface", cat="Surface"),
        dict(k="iu", t="Interpolate", cat="Curve"),
        dict(k="fm", t="Flip Matrix", cat="Sets"),
        dict(k="iv", t="Interpolate", cat="Curve"),
        dict(k="mg", t="Merge", cat="Sets"),
        dict(k="pp", t="Pipe", cat="Surface"),
    ],
    "wires": [
        ("Wd", None, "dy", "B", ""), ("dy", None, "ys", "D", ""),
        # crown height of each arch: Crown + Wave * sin(pi * y / W)
        ("ys", None, "yw", "A", ""), ("Wd", None, "yw", "B", ""), ("yw", None, "pi", "N", ""), ("pi", None, "sn", "x", ""),
        ("sn", None, "amp", "A", ""), ("Am", None, "amp", "B", ""), ("amp", None, "zc", "A", ""), ("Hc", None, "zc", "B", ""),
        ("L", None, "half", "A", ""),
        ("ys", None, "pa", "Y", ""),
        ("half", None, "pb", "X", ""), ("ys", None, "pb", "Y", ""), ("zc", None, "pb", "Z", ""),
        ("L", None, "pc", "X", ""), ("ys", None, "pc", "Y", ""),
        ("pa", None, "arc", "A", ""), ("pb", None, "arc", "B", ""), ("pc", None, "arc", "C", ""),
        ("arc", None, "lf", "C", ""), ("lo", None, "lf", "O", ""),   # rebuilt sections → one surface (arcs alone loft into a polysurface)
        ("lf", None, "sp", "S", ""), ("Nu", None, "sp", "U", ""), ("Nv", None, "sp", "V", ""),
        ("sp", "P", "iu", "V", ""), ("sp", "P", "fm", "D", ""), ("fm", None, "iv", "V", ""),
        ("iu", "C", "mg", "D1", "f"), ("iv", "C", "mg", "D2", "f"),
        ("mg", None, "pp", "C", ""), ("Rp", None, "pp", "R", ""),
    ],
    "values": [("dy", "A", 0), ("ys", "N", 2), ("half", "B", 2), ("lo", "Rbd", 12)],
    "exprs": [],
    "groups": [
        ("Inputs", "#DBABFF", ["L", "Wd", "Hc", "Am", "Nu", "Nv", "Rp"]),
        ("Arches", "#ABDBFF", ["dy", "ys", "yw", "pi", "sn", "amp", "zc", "half", "pa", "pb", "pc", "arc"]),
        ("Shell", "#FFD6AB", ["lo", "lf", "sp", "iu", "fm", "iv", "mg"]),
        ("Members", "#B2FAB4", ["pp"]),
    ],
    "check": ("pp", "P"),
}


def by_key(spec):
    return {n["k"]: n for n in spec["nodes"]}


def ordered_keys(spec):
    """Topological order of nodes (sources first)."""
    deps = {n["k"]: set() for n in spec["nodes"]}
    for s, _, d, _, _ in spec["wires"]:
        deps[d].add(s)
    out, done = [], set()
    while len(out) < len(deps):
        for k in deps:
            if k not in done and deps[k] <= done:
                out.append(k); done.add(k)
                break
        else:
            raise ValueError("cycle")
    return out


def ranks(spec):
    r = {}
    for k in ordered_keys(spec):
        srcs = [s for s, _, d, _, _ in spec["wires"] if d == k]
        r[k] = 1 + max((r[s] for s in srcs), default=-1)
    return r


# ----------------------------------------------------------------- Nextra shorthand

def shorthand(spec):
    nodes = by_key(spec)
    ins = {k: [] for k in nodes}
    for s, sp, d, dp, fl in spec["wires"]:
        ins[d].append((dp, s + ("." + sp if sp else ""), fl))
    lines, seen = [], set()

    def node_line(k):
        n = nodes[k]
        t = n["t"]
        head = f"{k} = {t}" + (f" @{n['sh_cat']}" if n.get("sh_cat") else "")
        parts, merged = [], {}
        for dp, src, fl in ins[k]:
            merged.setdefault(dp, ([], ""))
            merged[dp] = (merged[dp][0] + [src], fl or merged[dp][1])
        for dp, (srcs, fl) in merged.items():
            parts.append(f"{dp}={'+'.join(srcs)}" + (f"^{fl}" if fl else ""))
        for d, p, v in spec["values"]:
            if d == k:
                parts.append(f"{p}={v}")
        cfg = []
        if n.get("slider"):
            s = n["slider"]
            cfg.append(f"v={s['v']}"); cfg.append(f"r={s['min']}..{s['max']}")
            if s.get("int"): cfg.append("int")
        if n.get("swatch"): cfg.append(f"c={n['swatch']}")
        if spec.get("expression") and spec["expression"][0] == k: cfg.append(f'expr="{spec["expression"][1]}"')
        line = head + (f" ({', '.join(parts)})" if parts else "") + (f" {{{', '.join(cfg)}}}" if cfg else "")
        if n.get("nick"): line += f' "{n["nick"]}"'
        return line

    order = ordered_keys(spec)
    for name, color, keys in spec["groups"]:
        lines.append(f"## {name} {color}")
        for k in [x for x in order if x in keys]:
            lines.append(node_line(k)); seen.add(k)
    rest = [k for k in order if k not in seen]
    if rest:
        lines.append("##")
        lines += [node_line(k) for k in rest]
    return "\n".join(lines)


# ----------------------------------------------------------------- McNeel g1_apply_graph

def mcneel_graph(spec, info):
    """info: key -> {guid, ambiguous, in, out} from resolve_comps."""
    nodes, rk = by_key(spec), ranks(spec)
    rows = {}
    pos = {}
    for k in ordered_keys(spec):
        c = rk[k]; rows[c] = rows.get(c, -1) + 1
        pos[k] = (c * 170, rows[c] * 70)
    sliders, comps = [], []
    for k in ordered_keys(spec):
        n = nodes[k]
        x, y = pos[k]
        if n.get("slider"):
            s = n["slider"]
            sliders.append({"Key": k, "Min": s["min"], "Value": s["v"], "Max": s["max"], "Type": "int" if s.get("int") else "float", "Name": n.get("nick", k), "X": x, "Y": y})
        else:
            i = info[k]
            comps.append({"Key": k, "Selector": i["guid"] if i["ambiguous"] else n["t"], "X": x, "Y": y})
    wires = []
    for s, sp, d, dp, fl in spec["wires"]:
        # sliders / swatches are single-output special objects: port "0"
        src_port = "0" if nodes[s].get("slider") or not info[s]["out"] else (sp or info[s]["out"][0])
        wires.append({"SrcKey": s, "Src": src_port, "DstKey": d, "Dst": dp})
    return {"sliders": sliders, "components": comps, "wires": wires, "solve": True}


FIX_HEAD = """import Grasshopper, System
from Grasshopper.Kernel import GH_DataMapping
from Grasshopper.Kernel.Special import GH_Group
from Grasshopper.Kernel.Types import GH_Number, GH_Integer
doc = Grasshopper.Instances.ActiveCanvas.Document
ids = %s
def o(k): return doc.FindObject(System.Guid(ids[k]), True)
def inp(k, nick): return [p for p in o(k).Params.Input if p.NickName == nick][0]
"""


def fix_script(spec, ids, need_flags=True, need_values=True, need_groups=True):
    """Python that applies what a graph call could not: literal inputs, flatten/graft, expression text, swatch, groups."""
    used = set()
    body = []
    if need_values:
        for d, p, v in spec["values"]:
            body.append(f"q = inp('{d}', '{p}'); q.PersistentData.Clear(); q.PersistentData.Append(GH_Integer({v}) if q.TypeName == 'Integer' else GH_Number({v}))"); used.add(d)
    if need_flags:
        for s, sp, d, dp, fl in spec["wires"]:
            if "f" in fl: body.append(f"inp('{d}', '{dp}').DataMapping = GH_DataMapping.Flatten"); used.add(d)
            if "g" in fl: body.append(f"inp('{d}', '{dp}').DataMapping = GH_DataMapping.Graft"); used.add(d)
    if spec.get("expression"):
        k, e = spec["expression"]; used.add(k)
        body.append(f"o('{k}').Expression = '{e}'")
    for n in spec["nodes"]:
        if n.get("swatch"):
            c = n["swatch"]
            body.append(f"o('{n['k']}').SwatchColour = System.Drawing.ColorTranslator.FromHtml('{c}')"); used.add(n["k"])
    if need_groups:
        body.append("def group(name, col, keys):\n    g = GH_Group(); g.NickName = name; g.Colour = System.Drawing.ColorTranslator.FromHtml(col)\n"
                    "    for k in keys: g.AddObject(System.Guid(ids[k]))\n    doc.AddObject(g, False)")
        for name, color, keys in spec["groups"]:
            body.append(f"group('{name}', '{color}', {json.dumps(keys)})"); used.update(keys)
    body.append("doc.NewSolution(True)\nprint('ok')")      # expire all: changed persistent data must recompute
    sub = {k: ids[k] for k in ids if k in used}
    return FIX_HEAD % json.dumps(sub) + "\n".join(body)


# ----------------------------------------------------------------- RhinoMCP gh_build_graph

def rhinomcp_graph(spec, info):
    nodes = by_key(spec)
    comps = []
    for k in ordered_keys(spec):
        n = nodes[k]
        if n.get("slider"):
            s = n["slider"]
            comps.append({"alias": k, "component_name": "Number Slider", "value": s["v"], "min": s["min"], "max": s["max"], **({"decimals": 0} if s.get("int") else {}), "nickname": n.get("nick", k)})
        else:
            i = info[k]
            comps.append({"alias": k, **({"component_guid": i["guid"]} if i["ambiguous"] else {"component_name": n["t"]})})
    conns = []
    for s, sp, d, dp, fl in spec["wires"]:
        c = {"source": s, "target": d, "target_input_index": info[d]["in"].index(dp)}
        if sp and not nodes[s].get("slider"):
            oi = info[s]["out"].index(sp)
            if oi: c["source_output_index"] = oi
        conns.append(c)
    # literal values: RhinoMCP refuses them on generic-type inputs ("Unsupported parameter type: Param_GenericObject"),
    # so those get a small slider wired in, as the model would do
    values = []
    for d, p, v in spec["values"]:
        i = info[d]["in"].index(p)
        if info[d].get("in_types", [None] * 99)[i] in ("Generic Data", "Data"):
            comps.append({"alias": f"v_{d}_{p}", "component_name": "Number Slider", "value": v, "min": min(0, v), "max": max(1, v * 2)})
            conns.append({"source": f"v_{d}_{p}", "target": d, "target_input_index": i})
        else:
            values.append({"target": d, "input_index": i, "value": v})
    rgb = lambda h: [int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16)]   # RhinoMCP wants an int array
    groups = [{"name": name, "members": keys, "color": rgb(color)} for name, color, keys in spec["groups"]]
    return {"components": comps, "connections": conns, "values": values or None, "groups": groups, "layout": {"enabled": True, "max_columns": 6}}
