"""Neutral specs for the v3 Grasshopper cases + emitters for each system.

Spec fields (beyond v2): values may be numbers or strings; exprs (dst, port, expression); state (key, name, index, value)
for saved settings (Karamba buttons / unit dropdown); menu (key, 'A>B'); refs (key, 'layer:X'); late (wires that can only
be made after a state/menu change creates the input) and late_values (literals on such inputs)."""
import json

TOWER = dict(
    name="tower",
    nodes=[dict(k="n", t="Number Slider", slider=dict(v=12, min=2, max=40, int=True), nick="Floors"),
           dict(k="fh", t="Number Slider", slider=dict(v=3500, min=2500, max=5000, int=True), nick="Floor h"),
           dict(k="w", t="Number Slider", slider=dict(v=20000, min=5000, max=40000, int=True), nick="Width"),
           dict(k="tw", t="Number Slider", slider=dict(v=45, min=0, max=180, int=True), nick="Twist"),
           dict(k="ser", t="Series", cat="Sets"), dict(k="pt", t="Construct Point", cat="Vector"), dict(k="pl", t="XY Plane", cat="Vector"),
           dict(k="rec", t="Rectangle", cat="Curve"), dict(k="ang", t="Range", cat="Sets"), dict(k="mul", t="Multiplication", cat="Maths"),
           dict(k="rad", t="Radians", cat="Maths"), dict(k="rot", t="Rotate", cat="Transform"), dict(k="lft", t="Loft", cat="Surface"),
           dict(k="cap", t="Cap Holes", cat="Surface")],
    wires=[("fh", None, "ser", "N", ""), ("n", None, "ser", "C", ""), ("ser", None, "pt", "Z", ""), ("pt", None, "pl", "O", ""),
           ("pl", None, "rec", "P", ""), ("w", None, "rec", "X", ""), ("w", None, "rec", "Y", ""), ("n", None, "ang", "N", ""),
           ("ang", None, "mul", "A", ""), ("tw", None, "mul", "B", ""), ("mul", None, "rad", "D", ""), ("rec", "R", "rot", "G", ""),
           ("rad", None, "rot", "A", ""), ("pl", None, "rot", "P", ""), ("rot", "G", "lft", "C", "f"), ("lft", None, "cap", "B", "")],
    values=[("ser", "S", 0)], exprs=[("ang", "N", "x-1")], state=[], menu=[], refs=[], late=[], late_values=[],
    groups=[("Inputs", "#DBABFF", ["n", "fh", "w", "tw"]), ("Floors", "#ABDBFF", ["ser", "pt", "pl", "rec"]),
            ("Twist", "#ABDBFF", ["ang", "mul", "rad", "rot"]), ("Massing", "#FFD6AB", ["lft", "cap"])],
)

FINS = dict(
    name="fins",
    nodes=[dict(k="slab", t="Brep", cat="Params", nick="Top slab"),
           dict(k="nf", t="Number Slider", slider=dict(v=24, min=4, max=80, int=True), nick="Fins"),
           dict(k="fd", t="Number Slider", slider=dict(v=900, min=200, max=2000, int=True), nick="Fin depth"),
           dict(k="fh", t="Number Slider", slider=dict(v=3200, min=1000, max=6000, int=True), nick="Fin height"),
           dict(k="zp", t="Construct Point", cat="Vector"), dict(k="pz", t="XY Plane", cat="Vector"), dict(k="sec", t="Brep | Plane", cat="Intersect"),
           dict(k="jn", t="Join Curves", cat="Curve"), dict(k="dv", t="Divide Curve", cat="Curve"), dict(k="ev", t="Evaluate Curve", cat="Curve"),
           dict(k="u1", t="Unit Z", cat="Vector"), dict(k="cp", t="Cross Product", cat="Vector"), dict(k="ln", t="Line SDL", cat="Curve"),
           dict(k="uz", t="Unit Z", cat="Vector"), dict(k="ext", t="Extrude", cat="Surface")],
    wires=[("zp", None, "pz", "O", ""), ("slab", None, "sec", "B", ""), ("pz", None, "sec", "P", ""), ("sec", "C", "jn", "C", "f"),
           ("jn", None, "dv", "C", ""), ("nf", None, "dv", "N", ""), ("jn", None, "ev", "C", ""), ("dv", "t", "ev", "t", ""),
           ("ev", "T", "cp", "A", ""), ("u1", None, "cp", "B", ""), ("dv", "P", "ln", "S", ""), ("cp", "V", "ln", "D", ""), ("fd", None, "ln", "L", ""),
           ("fh", None, "uz", "F", ""), ("ln", None, "ext", "B", ""), ("uz", None, "ext", "D", "")],
    values=[("zp", "Z", 13875)], exprs=[("uz", "F", "-x")], state=[], menu=[], refs=[("slab", "layer:Slab")], late=[], late_values=[],
    groups=[("Fins input", "#DBABFF", ["slab", "nf", "fd", "fh"]), ("Fins", "#FFD6AB", ["zp", "pz", "sec", "jn", "dv", "ev", "u1", "cp", "ln", "uz", "ext"])],
)

KBEAM = dict(
    name="kbeam",
    nodes=[dict(k="pa", t="Construct Point", cat="Vector"), dict(k="pb", t="Construct Point", cat="Vector"), dict(k="ln", t="Line", cat="Curve"),
           dict(k="sh", t="Shatter", cat="Curve"), dict(k="el", t="Create Linear Element", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="sup", t="Support", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="pm", t="Construct Point", cat="Vector"),
           dict(k="fv", t="Vector XYZ", cat="Vector"), dict(k="ld", t="Loads", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="asm", t="Assemble Model", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="an", t="Analyse", cat="Karamba3D", sh_cat="Karamba3D")],
    wires=[("pa", None, "ln", "A", ""), ("pb", None, "ln", "B", ""), ("ln", None, "sh", "C", ""), ("sh", None, "el", "Line", ""),
           ("pa", None, "sup", "Pos|Ind", ""), ("pb", None, "sup", "Pos|Ind", ""),
           ("el", "Elem", "asm", "Elem", ""), ("sup", None, "asm", "Support", ""), ("ld", None, "asm", "Load", ""), ("asm", "Model", "an", "Model", "")],
    values=[("pb", "X", 10), ("sh", "t", 0.5), ("pm", "X", 5), ("fv", "Z", -10)], exprs=[],
    state=[("sup", "button", 0, True), ("sup", "button", 1, True), ("sup", "button", 2, True), ("sup", "button", 3, True), ("ld", "unitname", -1, "Point")],
    menu=[], refs=[], late=[("pm", None, "ld", "Pos|Ind", ""), ("fv", "V", "ld", "Force", "")], late_values=[],
    groups=[("Beam geometry", "#DBABFF", ["pa", "pb", "ln", "sh"]), ("Karamba model", "#ABDBFF", ["el", "sup", "pm", "fv", "ld", "asm", "an"])],
    reparam=[("sh", "C")],
)

HOUSE = dict(
    name="house_fe",
    nodes=[dict(k="lns", t="Curve", cat="Params", nick="FE lines"), dict(k="shs", t="Brep", cat="Params", nick="FE shells"),
           dict(k="mr", t="Number Slider", slider=dict(v=3.5, min=0.2, max=6), nick="Mesh size m"),   # coarse: the Karamba trial allows 50 shells
           dict(k="mat", t="Material Selection", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="csb", t="Cross-section", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="css", t="Cross-section", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="el", t="Create Linear Element", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="mb", t="Mesh Breps", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="se", t="Create Surface Element", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="mv", t="Deconstruct Mesh", cat="Mesh"),
           dict(k="all", t="Merge", cat="Sets"), dict(k="cd", t="Cull Duplicates", cat="Vector"), dict(k="dz", t="Deconstruct", cat="Vector", sh_cat="Vector"),
           dict(k="lt", t="Smaller Than", cat="Maths"), dict(k="base", t="Cull Pattern", cat="Sets"),
           dict(k="sup", t="Support", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="g", t="Loads", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="asm", t="Assemble Model", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="an", t="Analyse", cat="Karamba3D", sh_cat="Karamba3D")],
    wires=[("mat", None, "csb", "Mat", ""), ("mat", None, "css", "Mat", ""), ("lns", None, "el", "Line", ""), ("csb", None, "el", "CroSec", ""),
           ("shs", None, "mb", "Brep", ""), ("el", "Pts", "mb", "IPts", "f"), ("mr", None, "mb", "MRes", ""), ("mb", "Mesh", "se", "Mesh", ""), ("css", None, "se", "CroSec", ""),
           ("mb", "Mesh", "mv", "M", ""), ("el", "Pts", "all", "D1", "f"), ("mv", "V", "all", "D2", "f"), ("all", None, "cd", "P", ""),
           ("cd", "P", "dz", "P", ""), ("dz", "Z", "lt", "A", ""), ("cd", "P", "base", "L", ""), ("lt", "<", "base", "P", ""), ("base", None, "sup", "Pos|Ind", ""),
           ("el", "Elem", "asm", "Elem", "f"), ("se", "Elem", "asm", "Elem", "f"), ("sup", None, "asm", "Support", "f"), ("g", None, "asm", "Load", "f"),
           ("asm", "Model", "an", "Model", "")],
    values=[("mat", "Name|Ind", "C30/37"), ("cd", "T", 0.001), ("lt", "B", 0.01)], exprs=[],
    state=[("sup", "button", i, True) for i in range(6)],
    menu=[("csb", "Units>Trapezoid"), ("css", "Units>Shell - Const")],
    refs=[("lns", "layer:FE::Lines"), ("shs", "layer:FE::Shells")], late=[],
    late_values=[("csb", "Height", 30), ("csb", "UpperWidth", 30), ("csb", "LowerWidth", 30), ("css", "Height", 20)],
    groups=[("FE geometry from Rhino", "#DBABFF", ["lns", "shs", "mr"]), ("Sections", "#E6E6A0", ["mat", "csb", "css"]),
            ("Elements", "#ABDBFF", ["el", "mb", "se"]), ("Supports at z=0", "#ABDBFF", ["mv", "all", "cd", "dz", "lt", "base", "sup"]),
            ("Model + analysis", "#FFD6AB", ["g", "asm", "an"])],
)

TREES = dict(
    name="trees",
    nodes=[dict(k="nx", t="Number Slider", slider=dict(v=10, min=3, max=30, int=True), nick="Nx"),
           dict(k="ny", t="Number Slider", slider=dict(v=6, min=2, max=20, int=True), nick="Ny"),
           dict(k="sp", t="Number Slider", slider=dict(v=2, min=0.5, max=5), nick="Spacing"),
           dict(k="amp", t="Number Slider", slider=dict(v=1.5, min=0, max=5), nick="Amp"),
           dict(k="sx", t="Series", cat="Sets"), dict(k="sy", t="Series", cat="Sets"), dict(k="pt", t="Construct Point", cat="Vector"),
           dict(k="dc", t="Deconstruct", cat="Vector", sh_cat="Vector"), dict(k="ad", t="Addition", cat="Maths"), dict(k="sn", t="Sine", cat="Maths"),
           dict(k="mz", t="Multiplication", cat="Maths"), dict(k="uz", t="Unit Z", cat="Vector"), dict(k="mv", t="Move", cat="Transform"),
           dict(k="rows", t="Interpolate", cat="Curve"), dict(k="fm", t="Flip Matrix", cat="Sets"), dict(k="cols", t="Interpolate", cat="Curve"),
           dict(k="lf", t="Loft", cat="Surface")],
    wires=[("sp", None, "sx", "N", ""), ("nx", None, "sx", "C", ""), ("sp", None, "sy", "N", ""), ("ny", None, "sy", "C", ""),
           ("sx", None, "pt", "X", ""), ("sy", None, "pt", "Y", "g"), ("pt", None, "dc", "P", ""), ("dc", "X", "ad", "A", ""), ("dc", "Y", "ad", "B", ""),
           ("ad", None, "sn", "x", ""), ("sn", None, "mz", "A", ""), ("amp", None, "mz", "B", ""), ("mz", None, "uz", "F", ""),
           ("pt", None, "mv", "G", ""), ("uz", None, "mv", "T", ""), ("mv", "G", "rows", "V", ""), ("mv", "G", "fm", "D", ""), ("fm", None, "cols", "V", ""),
           ("rows", "C", "lf", "C", "f")],
    values=[("sx", "S", 0), ("sy", "S", 0)], exprs=[], state=[], menu=[], refs=[], late=[], late_values=[],
    groups=[("Inputs", "#DBABFF", ["nx", "ny", "sp", "amp"]), ("Grid tree", "#ABDBFF", ["sx", "sy", "pt", "dc", "ad", "sn", "mz", "uz", "mv"]),
            ("Surface", "#FFD6AB", ["rows", "fm", "cols", "lf"])],
)

KSHELL = dict(
    name="kshell",
    nodes=[dict(k="L", t="Number Slider", slider=dict(v=10, min=4, max=20), nick="Span m"),
           dict(k="W", t="Number Slider", slider=dict(v=6, min=2, max=12), nick="Width m"),
           dict(k="th", t="Number Slider", slider=dict(v=20, min=10, max=40, int=True), nick="Thickness cm"),
           dict(k="mr", t="Number Slider", slider=dict(v=2, min=1, max=4), nick="Mesh m"),
           dict(k="pl", t="XY Plane", cat="Vector"), dict(k="rc", t="Rectangle", cat="Curve"), dict(k="bs", t="Boundary Surfaces", cat="Surface"),
           dict(k="mb", t="Mesh Breps", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="mat", t="Material Selection", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="cs", t="Cross-section", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="se", t="Create Surface Element", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="dm", t="Deconstruct Mesh", cat="Mesh"), dict(k="dp", t="Deconstruct", cat="Vector", sh_cat="Vector"),
           dict(k="lt", t="Smaller Than", cat="Maths"), dict(k="gt", t="Larger Than", cat="Maths"), dict(k="orr", t="Gate Or", cat="Maths"),
           dict(k="base", t="Cull Pattern", cat="Sets"), dict(k="sup", t="Support", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="ld", t="Loads", cat="Karamba3D", sh_cat="Karamba3D"), dict(k="asm", t="Assemble Model", cat="Karamba3D", sh_cat="Karamba3D"),
           dict(k="an", t="Analyse", cat="Karamba3D", sh_cat="Karamba3D")],
    wires=[("pl", None, "rc", "P", ""), ("L", None, "rc", "X", ""), ("W", None, "rc", "Y", ""), ("rc", "R", "bs", "E", ""), ("bs", None, "mb", "Brep", ""),
           ("mr", None, "mb", "MRes", ""), ("mat", None, "cs", "Mat", ""), ("mb", "Mesh", "se", "Mesh", ""), ("cs", None, "se", "CroSec", ""),
           ("mb", "Mesh", "dm", "M", ""), ("dm", "V", "dp", "P", ""), ("dp", "X", "lt", "A", ""), ("dp", "X", "gt", "A", ""), ("L", None, "gt", "B", ""),
           ("lt", "<", "orr", "A", ""), ("gt", ">", "orr", "B", ""), ("dm", "V", "base", "L", ""), ("orr", None, "base", "P", ""), ("base", None, "sup", "Pos|Ind", ""),
           ("se", "Elem", "asm", "Elem", ""), ("sup", None, "asm", "Support", "f"), ("ld", None, "asm", "Load", ""), ("asm", "Model", "an", "Model", "")],
    values=[("mat", "Name|Ind", "C30/37"), ("lt", "B", 0.01)], exprs=[("gt", "B", "x-0.01")],
    state=[("sup", "button", i, True) for i in range(3)], menu=[("cs", "Units>Shell - Const")], refs=[],
    late=[("th", None, "cs", "Height", "")], late_values=[],
    groups=[("Inputs", "#DBABFF", ["L", "W", "th", "mr"]), ("Plate", "#ABDBFF", ["pl", "rc", "bs", "mb", "mat", "cs", "se"]),
            ("Supports on the short edges", "#ABDBFF", ["dm", "dp", "lt", "gt", "orr", "base", "sup"]), ("Analysis", "#FFD6AB", ["ld", "asm", "an"])],
)


def by_key(spec):
    return {n["k"]: n for n in spec["nodes"]}


def all_wires(spec):
    return spec["wires"] + spec.get("late", [])


def ordered_keys(spec):
    deps = {n["k"]: set() for n in spec["nodes"]}
    for s, _, d, _, _ in all_wires(spec):
        deps[d].add(s)
    out, done = [], set()
    while len(out) < len(deps):
        k = next(k for k in deps if k not in done and deps[k] <= done)
        out.append(k); done.add(k)
    return out


def lit(v):
    return f'"{v}"' if isinstance(v, str) else str(v)


# ----------------------------------------------------------------- Nextra shorthand

def shorthand(spec):
    nodes = by_key(spec)
    ins = {k: {} for k in nodes}
    for s, sp, d, dp, fl in all_wires(spec):
        srcs, f0 = ins[d].get(dp, ([], ""))
        ins[d][dp] = (srcs + [s + ("." + sp if sp else "")], fl or f0)
    exprs = {(d, p): e for d, p, e in spec["exprs"]}
    reparam = {(d, p) for d, p in spec.get("reparam", [])}

    def line(k):
        n = nodes[k]
        head = f"{k} = {n['t']}" + (f" @{n['sh_cat']}" if n.get("sh_cat") else "")
        parts = []
        for dp, (srcs, fl) in ins[k].items():
            fl = fl + ("p" if (k, dp) in reparam else "")
            parts.append(f"{dp}={'+'.join(srcs)}" + (f"^{fl}" if fl else "") + (f"^e({exprs[(k, dp)]})" if (k, dp) in exprs else ""))
        for d, p, v in spec["values"] + spec.get("late_values", []):
            if d == k: parts.append(f"{p}={lit(v)}")
        for (d, p), e in exprs.items():
            if d == k and p not in ins[k]: parts.append(f"{p}=^e({e})")
        cfg = []
        if n.get("slider"):
            s = n["slider"]; cfg += [f"v={s['v']}", f"r={s['min']}..{s['max']}"] + (["int"] if s.get("int") else [])
        for kk, name, idx, v in spec["state"]:
            if kk == k: cfg.append(f"${name}" + (f"[{idx}]" if idx >= 0 else "") + "=" + ("true" if v is True else "false" if v is False else lit(v)))
        for kk, path in spec["menu"]:
            if kk == k: cfg.append(f'menu="{path}"')
        for kk, f in spec["refs"]:
            if kk == k: cfg.append(f'ref="{f}"')
        out = head + (f" ({', '.join(parts)})" if parts else "") + (f" {{{', '.join(cfg)}}}" if cfg else "")
        if n.get("nick") and not n.get("slider"): out += f' "{n["nick"]}"'
        elif n.get("nick"): out += f' "{n["nick"]}"'
        return out

    order = ordered_keys(spec)
    lines, seen = [], set()
    for name, color, keys in spec["groups"]:
        lines.append(f"## {name} {color}")
        for k in [x for x in order if x in keys]:
            lines.append(line(k)); seen.add(k)
    rest = [k for k in order if k not in seen]
    if rest: lines += ["##"] + [line(k) for k in rest]
    return "\n".join(lines)


# ----------------------------------------------------------------- McNeel / RhinoMCP

def positions(spec):
    r, rows, pos = {}, {}, {}
    for k in ordered_keys(spec):
        srcs = [s for s, _, d, _, _ in all_wires(spec) if d == k]
        r[k] = 1 + max((r[s] for s in srcs), default=-1)
        rows[r[k]] = rows.get(r[k], -1) + 1
        pos[k] = (r[k] * 170, rows[r[k]] * 70)
    return pos


def special(info, k):
    """sliders / panels / swatches / params without port lists → port '0' / index 0"""
    return not info.get(k) or not info[k]["out"]


def mcneel_graph(spec, info, selector):
    nodes, pos = by_key(spec), positions(spec)
    sliders, comps = [], []
    for k in ordered_keys(spec):
        n = nodes[k]; x, y = pos[k]
        if n.get("slider"):
            s = n["slider"]
            sliders.append({"Key": k, "Min": s["min"], "Value": s["v"], "Max": s["max"], "Type": "int" if s.get("int") else "float", "Name": n.get("nick", k), "X": x, "Y": y})
        else:
            comps.append({"Key": k, "Selector": selector(n, info[k]), "X": x, "Y": y})
    wires = [{"SrcKey": s, "Src": "0" if nodes[s].get("slider") or special(info, s) else (sp or info[s]["out"][0]), "DstKey": d, "Dst": dp}
             for s, sp, d, dp, fl in spec["wires"]]
    return {"sliders": sliders, "components": comps, "wires": wires, "solve": True}


def rhinomcp_graph(spec, info):
    nodes = by_key(spec)
    comps, conns, values = [], [], []
    for k in ordered_keys(spec):
        n = nodes[k]
        if n.get("slider"):
            s = n["slider"]
            comps.append({"alias": k, "component_name": "Number Slider", "value": s["v"], "min": s["min"], "max": s["max"], **({"decimals": 0} if s.get("int") else {}), "nickname": n.get("nick", k)})
        else:
            i = info[k]
            comps.append({"alias": k, **({"component_guid": i["guid"]} if i["ambiguous"] else {"component_name": n["t"]})})
    for s, sp, d, dp, fl in spec["wires"]:
        c = {"source": s, "target": d, "target_input_index": info[d]["in"].index(dp)}
        if sp and not nodes[s].get("slider") and not special(info, s):
            oi = info[s]["out"].index(sp)
            if oi: c["source_output_index"] = oi
        conns.append(c)
    py_values = []
    for d, p, v in spec["values"]:
        i = info[d]["in"].index(p)
        if info[d].get("in_types", [None] * 99)[i] in ("Generic Data", "Data"):
            py_values.append((d, p, v))           # refused on generic inputs: set by the python fix
        else:
            values.append({"target": d, "input_index": i, "value": v})
    rgb = lambda h: [int(h[1:3], 16), int(h[3:5], 16), int(h[5:7], 16)]
    groups = [{"name": name, "members": keys, "color": rgb(color)} for name, color, keys in spec["groups"]]
    return {"components": comps, "connections": conns, "values": values or None, "groups": groups, "layout": {"enabled": True, "max_columns": 6}}, py_values


def fix_body(spec, ids, values=True, groups=True, extra_values=()):
    """Case-specific lines for the python fix (helpers come from pyhelpers)."""
    L = []
    if values:
        for d, p, v in spec["values"]:
            L.append(f"literal('{ids[d]}', '{p}', {json.dumps(v)})" if not isinstance(v, str) else f"inp('{ids[d]}', '{p}').PersistentData.Clear(); inp('{ids[d]}', '{p}').PersistentData.Append(Grasshopper.Kernel.Types.GH_String({json.dumps(v)}))")
    for d, p, v in extra_values:
        L.append(f"literal('{ids[d]}', '{p}', {json.dumps(v)})" if not isinstance(v, str) else f"inp('{ids[d]}', '{p}').PersistentData.Clear(); inp('{ids[d]}', '{p}').PersistentData.Append(Grasshopper.Kernel.Types.GH_String({json.dumps(v)}))")
    for s, sp, d, dp, fl in spec["wires"]:
        for f in fl:
            L.append(f"flag('{ids[d]}', '{dp}', '{f}')")
    for d, p in spec.get("reparam", []):
        L.append(f"inp('{ids[d]}', '{p}').Reparameterize = True")
    for d, p, e in spec["exprs"]:
        L.append(f"expr('{ids[d]}', '{p}', '{e}')")
    for k, name, idx, v in spec["state"]:
        L.append(f"set_state('{ids[k]}', '{name}', {idx}, {json.dumps(v) if not isinstance(v, bool) else str(v)})")
    for k, path in spec["menu"]:
        L.append(f"menu('{ids[k]}', '{path}')")
    for k, f in spec["refs"]:
        L.append(f"ref_layer('{ids[k]}', '{f.split(':', 1)[1]}')")
    for d, p, v in spec.get("late_values", []):
        L.append(f"literal('{ids[d]}', '{p}', {v})")
    if groups:
        for name, color, keys in spec["groups"]:
            L.append(f"group('{name}', '{color}', {json.dumps([ids[k] for k in keys])})")
    return "\n".join(L)
