"""Full regression: every case from scratch, in ONE Rhino session, in order, with checks.

usage:  python test/regress/regress.py            # all cases
        python test/regress/regress.py 03 12       # only cases whose id starts with these
Needs a running Rhino with Nextra. Writes replies + images to test/regress/out/<case>/ and a summary
to test/regress/out/SUMMARY.md. Exit code 1 if any check failed.
"""
import json, os, re, shutil, subprocess, sys, time

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(REPO, "tools"))
import nx  # noqa: E402

OUT = os.path.join(HERE, "out")
SCR = os.path.join(HERE, "scripts")
TMP = os.path.join(HERE, "_tmp")
STEPS = []


def script(name):
    with open(os.path.join(SCR, name), encoding="utf-8") as f:
        return f.read()


def ids(text):
    """'ids: a=g1 b=g2' → {'a':'g1','b':'g2'} (also groups: line)."""
    res = {}
    for line in text.splitlines():
        if line.startswith("ids:") or line.startswith("groups:"):
            for m in re.finditer(r"(\S[^=\s]*(?: [^=\s]+)*)=(g\d+)", line.split(":", 1)[1]):
                res[m.group(1).strip()] = m.group(2)
    return res


def num_after(text, pattern):
    m = re.search(pattern, text)
    return float(m.group(1)) if m else None


class Case:
    def __init__(self, cid, title):
        self.cid, self.title, self.n = cid, title, 0
        self.dir = os.path.join(OUT, cid)
        shutil.rmtree(self.dir, ignore_errors=True)
        os.makedirs(self.dir, exist_ok=True)
        print(f"\n=== {cid} {title}")

    def call(self, tool, args, expect=(), forbid=("ERROR", "internal engine error"), ok=True, timeout=120, label=""):
        self.n += 1
        args = dict(args)
        if "timeout" in args:          # like the bridge: per-call timeout is not an engine arg
            timeout = int(args.pop("timeout"))
        t0 = time.time()
        try:
            res, ti, to, _ = nx.call(tool, args, timeout, log=False)
        except Exception as e:  # transport failure is a failed step, not a crash of the suite
            res, ti, to = {"ok": False, "text": f"TRANSPORT: {e}"}, 0, 0
        dt = time.time() - t0
        text = res.get("text", "")
        probs = []
        if ok is not None and bool(res.get("ok")) != ok:
            probs.append(f"ok={res.get('ok')} (expected {ok})")
        for e in expect:
            if not re.search(e, text, re.S):
                probs.append(f"missing /{e}/")
        for f in forbid or ():
            if f in text:
                probs.append(f"contains '{f}'")
        imgs = []
        for i, p in enumerate(res.get("images", [])):
            dst = os.path.join(self.dir, f"{self.n:02d}_{tool}_{i}.png")
            try:
                shutil.copy(p, dst); imgs.append(dst)
            except Exception:
                probs.append("image missing " + p)
        with open(os.path.join(self.dir, f"{self.n:02d}_{tool}.txt"), "w", encoding="utf-8") as f:
            f.write(json.dumps(args, ensure_ascii=False, indent=1) + "\n---\n" + text + "\n")
        STEPS.append({"case": self.cid, "n": self.n, "tool": tool, "label": label, "ok": not probs, "probs": probs,
                      "tin": ti, "tout": to, "sec": round(dt, 2), "first": text.splitlines()[0] if text else ""})
        flag = "PASS" if not probs else "FAIL"
        print(f"  [{flag}] {self.n:02d} {tool} {label} ({dt:.1f}s, ~{to} tok)" + ("" if not probs else "\n        " + "; ".join(probs) + "\n        " + text[:400].replace("\n", "\n        ")))
        return text

    def check(self, cond, label, detail=""):
        self.n += 1
        STEPS.append({"case": self.cid, "n": self.n, "tool": "check", "label": label, "ok": bool(cond), "probs": [] if cond else [detail or "failed"],
                      "tin": 0, "tout": 0, "sec": 0, "first": detail})
        print(f"  [{'PASS' if cond else 'FAIL'}] {self.n:02d} check {label} {detail}")


RESET = """
for o in list(doc.Objects):
    doc.Objects.Delete(o, True)
for i in range(doc.Layers.Count - 1, -1, -1):
    ly = doc.Layers[i]
    if not ly.IsDeleted and ly.Name != "Default":
        doc.Layers.Purge(i, True)
doc.ModelUnitSystem = Rhino.UnitSystem.Millimeters
doc.Views.Redraw()
print("reset", len(list(doc.Objects)))
"""


def reset(c):
    c.call("run", {"code": RESET, "label": "reset"}, expect=[r"reset 0"], label="reset doc")


# ------------------------------------------------------------------ cases

def c00():
    c = Case("00_session", "session start, status, docs, memory empty")
    c.call("query", {"scope": "status"}, expect=[r"engine", r"idle"])
    c.call("memory", {"action": "list"}, expect=[r"prefs", r"library"])
    c.call("query", {"scope": "docs"}, expect=[r"shorthand", r"workflows"])
    c.call("query", {"scope": "docs", "filter": "shorthand"}, expect=[r"## Create", r"\^f"])
    c.call("query", {"scope": "components", "filter": "loft, divide curve"}, expect=[r"Loft", r"Divide Curve.*in:"])
    c.call("query", {"scope": "components", "filter": "@Karamba3D"}, expect=[r"Karamba3D", r"plugin rules \(Karamba3D"])


def c01():
    c = Case("01_rhino_scratch", "3-storey frame from scratch (+ save_as = case 09)")
    reset(c)
    c.call("query", {}, expect=[r"units millimeters", r"0 objects"])
    t = c.call("run", {"code": script("frame_building.py"), "args": {"storeys": 3}, "save_as": "frame_building", "label": "3-storey frame"},
               expect=[r"checkpoint c\d+", r"rhino delta \+90", r"\[Columns\]", r"\[Beams\]", r"\[Slabs\]", r"library"])
    c.call("query", {}, expect=[r"90 objects", r"Columns", r"Beams", r"Slabs"])
    c.call("query", {"filter": "layer:Slabs"}, expect=[r"planar n\+z", r"S_L3"])
    c.call("query", {"filter": "name:C00_L1", "level": "detail"}, expect=[r"C00_L1", r"faces"])
    c.call("query", {"filter": "name:C00_L1", "level": "verts"}, expect=[r"\(0,0|\(-200"])
    c.call("view", {"view": "iso"}, expect=[r"Grid A-H x 1-8"])
    c.call("view", {"view": "iso", "focus": "layer:Slabs"}, expect=[r"tagged"])
    c.call("view", {"view": "top", "focus": "C3:E5"}, expect=[r"region C3:E5"])
    c.call("query", {"filter": "cells:C3:E5", "limit": 5}, expect=[r"object"])


def c02():
    c = Case("02_rhino_modify", "add storey via library (case 10), move it, undo exactly")
    c.call("query", {"scope": "library"}, expect=[r"frame_building\.py"])
    c.call("run", {"lib": "frame_building", "args": {"storeys": 1, "z0": 10500}, "label": "add storey 4"},
           expect=[r"storeys 4\.\.4", r"rhino delta \+30", r"×12"])
    t = c.call("run", {"code": 'xf = rg.Transform.Translation(3000, 0, 0)\nfor o in q("bbox:-1000,-1000,10600,30000,20000,15000"):\n    doc.Objects.Transform(o, xf, True)', "label": "shift storey 4"},
               expect=[r"rhino delta ~30"])
    ck = re.search(r"checkpoint (c\d+)", t)
    s1 = c.call("query", {"filter": "name:S_L4"}, expect=[r"@\(12000,"])
    c.call("undo", {"list": True}, expect=[r"c\d+"])
    if ck:
        c.call("undo", {"checkpoint": ck.group(1)}, expect=[r"reverted"])
    c.call("query", {"filter": "name:S_L4"}, expect=[r"@\(9000,"])
    # error path: helper misuse comes back as a short message + hint, then a 1-line patch fixes it
    c.call("run", {"code": 'x = q("bbox:1,2")\nprint(len(x))'}, ok=False, expect=[r"bbox"], forbid=("internal engine error",))
    c.call("run", {"patch": [{"find": "bbox:1,2", "replace": "layer:Slabs"}]}, expect=[r"\b4\b"])
    c.call("run", {"patch": [{"find": "does-not-exist", "replace": "x"}]}, ok=False, expect=[r"find"], forbid=("internal engine error",))


def c03():
    c = Case("03_gh_scratch", "twisting tower from scratch")
    t = c.call("gh_edit", {"new": True, "ops": """## Inputs #DBABFF
n = Number Slider {v=12, r=2..40, int} "Floors"
fh = Number Slider {v=3500, r=2500..5000, int} "Floor h"
w = Number Slider {v=20000, r=5000..40000, int} "Width"
tw = Number Slider {v=45, r=0..180, int} "Twist"
## Floors #ABDBFF
ser = Series (S=0, N=fh, C=n)
pt = Construct Point (Z=ser)
pl = XY Plane (O=pt)
rec = Rectangle (P=pl, X=w, Y=w)
## Twist #ABDBFF
ang = Range (D=0..1, N=n^e(x-1))
mul = Multiplication (A=ang, B=tw)
rad = Radians (D=mul)
rot = Rotate (G=rec.R, A=rad, P=pl)
## Massing #FFD6AB
lft = Loft (C=rot.G^f)
cap = Cap Holes (B=lft)"""}, expect=[r"ok: 14/14", r"\+4 groups", r"1 Brep"])
    m = ids(t)
    CTX["tower"] = m
    c.call("query", {"scope": "gh"}, expect=[r"4 groups"])
    c.call("query", {"scope": "gh", "level": "list"}, expect=[r"## Inputs", r"Loft", r"\^f"])
    c.call("query", {"scope": "gh", "filter": "group:Twist", "data": True}, expect=[r"=> "])
    c.call("view", {"target": "gh"}, expect=[r"gh canvas", r"tagged"])
    c.call("view", {"target": "gh_preview", "view": "iso"}, expect=[r"GH preview"])
    c.call("query", {"scope": "gh", "filter": m.get("rot", "g12"), "level": "detail"}, expect=[r"inputs:", r"outputs:"])


def c04():
    c = Case("04_gh_modify", "rectangle → circle inside its group, nothing else moves")
    m = CTX.get("tower", {})
    before = c.call("query", {"scope": "gh", "filter": "group:Inputs", "level": "detail"})
    t = c.call("gh_edit", {"ops": f"- {m['rec']}\n## Floors\ncir = Circle (P={m['pl']}, R={m['w']}^e(x/2))\n{m['rot']} (G=cir)"},
               expect=[r"ok: 3/3", r"-1 deleted", r"results: .*1 Brep"])
    after = c.call("query", {"scope": "gh", "filter": "group:Inputs", "level": "detail"})
    c.check(before == after, "inputs group unchanged after modify")
    c.call("gh_edit", {"ops": f"{m['n']} {{v=8}}"}, expect=[r"ok: 1/1", r"results: .*Brep"])
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])


def c05():
    c = Case("05_rhino_gh", "Rhino slab → GH fins → bake")
    reset(c)
    t = c.call("run", {"code": "doc.Objects.AddBrep(rg.Box(rg.Plane.WorldXY, rg.Interval(-300,18300), rg.Interval(-300,12300), rg.Interval(13750,14000)).ToBrep())", "label": "slab"},
               expect=[r"\+ r\d+ polysurface"])
    slab = re.search(r"\+ (r\d+)", t).group(1)
    t = c.call("gh_edit", {"new": True, "ops": f"""## Fins input #DBABFF
slab = Brep {{ref=[{slab}]}} "Top slab"
nf = Number Slider {{v=24, r=4..80, int}} "Fins"
fd = Number Slider {{v=900, r=200..2000, int}} "Fin depth"
fh = Number Slider {{v=3200, r=1000..6000, int}} "Fin height"
## Fins #FFD6AB
pz = XY Plane (O={{0,0,13875}})
sec = Brep | Plane (B=slab, P=pz)
jn = Join Curves (C=sec.C^f)
dv = Divide Curve (C=jn, N=nf)
ev = Evaluate Curve (C=jn, t=dv.t)
cp = Cross Product (A=ev.T, B={{0,0,1}})
ln = Line SDL (S=dv.P, D=cp.V, L=fd)
uz = Unit Z (F=fh^e(-x))
ext = Extrude (B=ln, D=uz)"""}, expect=[r"ok: 13/13", r"24 (Surface|Brep|Extrusion)"])
    m = ids(t)
    c.call("view", {"target": "gh_preview", "focus": m.get("ext", "g13"), "view": "iso"}, expect=[r"GH preview"])
    c.call("gh_edit", {"bake": [m.get("ext", "g13")], "layer": "Fins"}, expect=[r"\+ \[Fins\]|\+24|×24"])
    c.call("query", {"filter": "layer:Fins", "limit": 3}, expect=[r"\[Fins\]"])
    c.call("query", {"scope": "gh", "filter": m.get("slab", "g1")}, expect=[r"ref="])


def c06():
    c = Case("06_gh_python", "Python 3 component: ports, error with line, patch")
    t = c.call("gh_edit", {"new": True, "ops": '''## Py demo #ABDBFF
cnt = Number Slider {v=8, r=3..30, int} "Count"
rad = Number Slider {v=5000, r=1000..20000, int} "Radius"
py = Python 3 Script (n=cnt, r=rad) -> (pts, crv) {code="""
import math
import Rhino.Geometry as rg
pts = [rg.Point3d(r*math.cos(2*math.pi*i/n), r*math.sin(2*math.pi*i/n), 0) for i in range(int(n))]
crv = rg.Polyline(pts + [pts[0]]).ToNurbsCurve()
print('made', len(pts))
"""}
out = Panel (=py.out)'''}, expect=[r"ok: 4/4", r"8 Point", r"1 Curve"])
    m = ids(t)
    CTX["py"] = m
    c.call("gh_edit", {"script": {"handle": m["py"], "patch": [{"find": "crv = rg.Polyline(pts + [pts[0]]).ToNurbsCurve()", "replace": "crv = rg.Polyline(pts + [pts[0]]).ToNurbsCurve()\nlen_total = crv.GetLength() / undefined_var"}]}},
           expect=[r"undefined_var", r"\[\d+:\d+\]"], ok=None, forbid=("internal engine error",))
    c.call("gh_edit", {"script": {"handle": m["py"], "patch": [{"find": "/ undefined_var", "replace": "/ 1000.0"}]}},
           expect=[r"no errors", r"8 Point"])
    c.call("gh_edit", {"script": {"handle": m["py"], "mirror": True}}, expect=[r"\.py"])
    c.call("gh_edit", {"script": {"handle": m["py"], "from_mirror": True}}, expect=[r"script"])
    c.call("query", {"scope": "gh", "filter": m["py"], "level": "detail"}, expect=[r"code|\"\"\""])


def c07():
    c = Case("07_gh_csharp", "C# component: ports from RunScript signature")
    m = CTX["py"]
    t = c.call("gh_edit", {"ops": f'''## C# demo #FFD6AB
cs = C# Script {{code="""
using System;
using System.Collections.Generic;
using Rhino.Geometry;
using Grasshopper.Kernel;

public class Script_Instance : GH_ScriptInstance
{{
    private void RunScript(Curve crv, int count, double height, ref object towers)
    {{
        var res = new List<Brep>();
        double[] ts = crv.DivideByCount(count, true);
        foreach (var t in ts)
        {{
            var p = crv.PointAt(t);
            var c = new Circle(new Plane(p, Vector3d.ZAxis), 600);
            res.Add(Extrusion.Create(c.ToNurbsCurve(), height, true).ToBrep());
        }}
        towers = res;
    }}
}}
"""}}
{m['py']}.crv > cs.crv
{m['cnt']} > cs.count
h = Number Slider {{v=9000, r=1000..30000, int}} "Tower h"
h > cs.height'''}, expect=[r"ok: 5/5", r"ports from RunScript", r"8 Brep"])
    cm = ids(t)
    # compile error comes back with line
    c.call("gh_edit", {"script": {"handle": cm["cs"], "patch": [{"find": "towers = res;", "replace": "towers = resX;"}]}},
           ok=None, expect=[r"resX"], forbid=("internal engine error",))
    c.call("gh_edit", {"script": {"handle": cm["cs"], "patch": [{"find": "towers = resX;", "replace": "towers = res;"}]}}, expect=[r"8 Brep"])


def c08():
    c = Case("08_radio", "Python component with on-component radio buttons")
    t = c.call("gh_edit", {"ops": f'''## Material picker #FFD6AB
radio = Python 3 Script -> (choice, index) {{code=@{os.path.join(SCR, "radio_component.py").replace(os.sep, "/")}}}
pn = Panel (=radio.choice)'''}, expect=[r"ok: 2/2"])
    m = ids(t)
    c.call("gh_edit", {"solve": True}, ok=None)
    c.call("query", {"scope": "gh", "filter": m["radio"], "data": True}, expect=[r"Steel"])
    c.call("view", {"target": "gh", "focus": m["radio"], "size": [600, 400]}, expect=[r"gh canvas"])
    # click the 3rd row (fractions measured on the component body)
    c.call("gh_edit", {"ops": f'{m["radio"]} {{click="0.5,0.80"}}'}, expect=[r"clicked"])
    t2 = c.call("query", {"scope": "gh", "filter": m["radio"], "data": True})
    c.check("Steel" not in t2.split("choice")[-1][:60] or "Timber" in t2 or "Concrete" in t2, "radio click changed the choice", t2[-160:].replace("\n", " | "))


def c09_10():
    c = Case("09_10_library", "library: save, list, read, run by name, file")
    c.call("memory", {"action": "save_script", "name": "hello_pts", "code": '"""Points on a line. args: n"""\nfor i in range(args.get("n", 3)):\n    doc.Objects.AddPoint(rg.Point3d(i*1000, 0, 0))', "doc": "points on a line"}, expect=[r"saved"])
    c.call("query", {"scope": "library"}, expect=[r"hello_pts", r"frame_building"])
    c.call("query", {"scope": "library", "name": "hello_pts"}, expect=[r"AddPoint"])
    c.call("run", {"lib": "hello_pts", "args": {"n": 4}}, expect=[r"\+4|\+ r\d+ point"])
    os.makedirs(TMP, exist_ok=True)
    p = os.path.join(TMP, "count.py")
    with open(p, "w", encoding="utf-8") as f:
        f.write("print('objects', doc.Objects.Count)")
    c.call("run", {"file": p}, expect=[r"objects \d+"])
    c.call("run", {"lib": "no_such_script"}, ok=False, expect=[r"no_such_script|not found"], forbid=("internal engine error",))
    c.call("memory", {"action": "delete_script", "name": "hello_pts"}, ok=None)


def c12():
    c = Case("12_karamba", "simple beam: 10 m, 10 kN mid-span → 47.04 cm")
    t = c.call("gh_edit", {"new": True, "ops": """## Beam geometry #DBABFF
a = Point {set={0,0,0}}
b = Point {set={10,0,0}}
ln = Line @Curve (A=a, B=b)
sh = Shatter (C=ln^p, t=0.5)
## Karamba model #ABDBFF
sup = Support @Karamba3D (Pos|Ind=[{0,0,0},{10,0,0}]) {$button[0]=true, $button[1]=true, $button[2]=true, $button[3]=true}
el = Create Linear Element @Karamba3D (Line=sh)
ld = Loads @Karamba3D {$ActiveUnit/unitname="Point"}
asm = Assemble Model @Karamba3D (Elem=el.Elem, Support=sup, Load=ld)
an = Analyse @Karamba3D (Model=asm.Model)
## Results #B2FAB4
pd = Panel (=an.MaxDisp) "Max disp cm"
pm = Panel (=asm.Mass) "Mass kg"
"""}, expect=[r"ok: 11/11"])
    m = ids(t)
    c.call("gh_edit", {"ops": f"{m['ld']} (Pos|Ind={{5,0,0}}, Force={{0,0,-10}})"}, expect=[r"ok: 1/1"], forbid=("ERROR", "plugin rules"))
    d = c.call("query", {"scope": "gh", "filter": f"{m['an']} {m['asm']}", "level": "data"}, expect=[r"MaxDisp"])
    v = num_after(d, r"MaxDisp: .*?e\.g\. ([\d.]+)")
    c.check(v is not None and abs(v - 47.04) < 0.2, "max displacement = 47.04 cm (PL³/48EI)", f"got {v}")
    c.call("query", {"scope": "gh", "filter": m["sup"], "level": "detail"}, expect=[r"\$|menu"])
    # component controls: right-click toggle on a built-in component
    t = c.call("gh_edit", {"ops": f"## Menu test #E6E6A0\nbb = Bounding Box (C={m['sh']})"}, expect=[r"2 Box"])
    bb = ids(t)["bb"]
    c.call("query", {"scope": "gh", "filter": bb, "level": "detail"}, expect=[r"menu: .*Union Box"])
    c.call("gh_edit", {"ops": f'{bb} {{menu="Union Box:on"}}'}, expect=[r"Union Box", r"1 Box"])
    c.call("gh_edit", {"ops": f'{bb} {{menu="Union Box:on"}}'}, expect=[r"1 Box"])   # idempotent
    c.call("gh_edit", {"ops": f'{bb} {{menu="Settings..."}}'}, ok=None, expect=[r"refused|dialog|not found|no menu"], forbid=("internal engine error",))
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])


def c13():
    c = Case("13_mbs", "Marina Bay Sands massing script → library")
    reset(c)
    c.call("run", {"code": script("mbs.py"), "args": {}, "save_as": "marina_bay_sands"}, expect=[r"MBS: 3 towers", r"\+8|rhino delta \+"])
    c.call("view", {"view": "persp"}, expect=[r"Grid"])
    c.call("query", {"filter": "name:Skypark"}, expect=[r"Skypark"])


def c14():
    c = Case("14_house_fe", "house solids → FE geometry → Karamba, mass check")
    reset(c)
    c.call("run", {"code": script("house_input.py")}, expect=[r"house:", r"\[ARCH::Walls\]"])
    c.call("query", {"filter": "layer:ARCH::*"}, expect=[r"linear vertical", r"planar"])
    c.call("run", {"code": script("solids_to_fe.py"), "save_as": "solids_to_fe"}, expect=[r"14 lines", r"6 shells", r"FE::Lines"])
    c.call("view", {"view": "iso", "focus": "layer:FE*", "isolate": True}, expect=[r"Grid"])
    t = c.call("gh_edit", {"new": True, "ops": """## FE geometry from Rhino #DBABFF
ln = Curve {ref="layer:FE::Lines"} "FE lines"
sh = Brep {ref="layer:FE::Shells"} "FE shells"
mr = Number Slider {v=3.5, r=0.2..6, d=2} "Mesh size m"
## Sections #E6E6A0
mat = Material Selection @Karamba3D (Name|Ind="C30/37")
csb = Cross-section @Karamba3D (Mat=mat, Height=30, UpperWidth=30, LowerWidth=30) {menu="Units>Trapezoid"}
css = Cross-section @Karamba3D (Mat=mat, Height=20) {menu="Units>Shell - Const"}
## Elements #ABDBFF
el = Create Linear Element @Karamba3D (Line=ln, CroSec=csb)
mb = Mesh Breps @Karamba3D (Brep=sh, IPts=el.Pts^f, MRes=mr)
se = Create Surface Element @Karamba3D (Mesh=mb.Mesh, CroSec=css)
## Supports at z=0 #ABDBFF
mv = Deconstruct Mesh (M=mb.Mesh)
all = Merge (D1=el.Pts^f, D2=mv.V^f)
cd = Cull Duplicates (P=all, T=0.001)
dz = Deconstruct @Vector (P=cd.P)
lt = Smaller Than (A=dz.Z, B=0.01)
base = Cull Pattern (L=cd.P, P=lt.0)
sup = Support @Karamba3D (Pos|Ind=base) {$button[0]=true, $button[1]=true, $button[2]=true, $button[3]=true, $button[4]=true, $button[5]=true}
## Model + analysis #FFD6AB
g = Loads @Karamba3D
asm = Assemble Model @Karamba3D (Elem=el.Elem+se.Elem^f, Support=sup^f, Load=g^f)
an = Analyse @Karamba3D (Model=asm.Model)
## Results #B2FAB4
pm = Panel (=asm.Mass) "Mass kg"
pd = Panel (=an.MaxDisp) "Max disp cm"
"""}, expect=[r"ok: 21/21"])
    m = ids(t)
    d = c.call("query", {"scope": "gh", "filter": f"{m['asm']} {m['an']}", "level": "data"}, expect=[r"Mass"])
    mass = num_after(d, r"Mass: .*?e\.g\. ([\d.]+)")
    c.check(mass is not None and 95000 < mass < 104000, "mass ≈ 99.5 t (hand check)", f"got {mass}")
    c.check("more than one support" not in d, "no duplicate supports")
    # the analysis must really run (the Karamba trial allows 50 shells: a fine mesh fails silently in Assemble)
    disp = num_after(d, r"MaxDisp: .*?e\.g\. ([\d.]+)")
    c.check(disp is not None and disp > 0 and "maximum number of shell" not in d, "analysis ran (max displacement > 0, within trial limit)", f"got {disp}")
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])
    c.call("view", {"target": "gh_preview", "view": "iso"}, expect=[r"GH preview"])
    os.makedirs(TMP, exist_ok=True)
    c.call("gh_edit", {"save": os.path.join(TMP, "house_karamba.gh")}, expect=[r"saved"])


def c15():
    c = Case("15_python_fallback", "explicit data tree via run → shorthand delta")
    c.call("gh_edit", {"new": True, "ops": "x = Number Slider {v=1}"}, expect=[r"ok"])
    t = c.call("run", {"code": script("tree_param.py"), "label": "explicit data tree"}, expect=[r"2 branches", r"gh delta \+1"])
    hd = re.search(r"(g\d+) 2 branches", t).group(1)
    t = c.call("gh_edit", {"ops": f"## Tree demo #E6E6A0\n{hd}\npn = Panel (={hd})\nml = Mass Addition (I={hd})"}, expect=[r"ok: 3/3", r"2 branches"])
    c.call("query", {"scope": "gh", "filter": "group:Tree demo", "data": True}, expect=[r"\{0;0\}|\{0;1\}|branches"])


def c16():
    c = Case("16_messy_tidy", "scrambled canvas → regroup → tidy → relays → save/open")
    t = c.call("gh_edit", {"new": True, "layout": "none", "ops": """w = Number Slider {v=30, r=5..60} "W"
h = Number Slider {v=12, r=3..40} "H"
nu = Number Slider {v=12, r=2..40, int} "U"
nv = Number Slider {v=6, r=2..20, int} "V"
amp = Number Slider {v=0.6, r=0..2} "Amp"
fq = Number Slider {v=2, r=0.5..6} "Freq"
d = Number Slider {v=0.8, r=0.1..2} "Depth"
o = Point {set={0,0,0}}
pl = XZ Plane (O=o)
rc = Rectangle (P=pl, X=w, Y=h)
bs = Boundary Surfaces (E=rc.R)
dv = Divide Domain² (I=bs, U=nu, V=nv)
iso = Isotrim (S=bs, D=dv)
ar = Area (G=iso)
cp = Deconstruct @Vector (P=ar.C)
m1 = Multiplication (A=cp.X, B=fq)
sn = Sine (x=m1)
m2 = Multiplication (A=sn, B=amp)
ab = Absolute (x=m2)
ad = Addition (A=ab, B=0.2)
sc = Scale (G=iso, C=ar.C, F=ad)
ed = Brep Edges (B=sc.G)
jn = Join Curves (C=ed.En)
nv2 = Unit Y (F=d)
ex = Extrude (B=jn, D=nv2)
cap = Cap Holes (B=ex)
col = Colour Swatch {c=#5A8CB4}
pv = Custom Preview (G=cap, M=col)
lst = List Length (L=iso)
pn = Panel (=lst)"""}, expect=[r"ok: 30/30"])
    m = ids(t)
    t = c.call("run", {"code": "import random, System\nrandom.seed(7)\nfor o in ghdoc().Objects:\n    o.Attributes.Pivot = System.Drawing.PointF(random.uniform(0, 1400), random.uniform(0, 900))\n    o.Attributes.ExpireLayout(); o.Attributes.PerformLayout()\n", "label": "scramble"},
               expect=[r"gh delta|gh: "])
    c.check("gh: no changes" not in t, "run reports GH layout moves", t.splitlines()[-1] if t else "")
    os.makedirs(TMP, exist_ok=True)
    c.call("gh_edit", {"save": os.path.join(TMP, "messy.gh")}, expect=[r"saved"])
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])
    c.call("query", {"scope": "gh"}, expect=[r"30|components"])
    g = lambda *ks: "\n".join(m[k] for k in ks)
    ops = ("## Inputs #DBABFF\n" + g("w", "h", "nu", "nv", "amp", "fq", "d", "o") +
           "\n## Base surface #ABDBFF\n" + g("pl", "rc", "bs") +
           "\n## Panel grid #ABDBFF\n" + g("dv", "iso", "ar", "lst", "pn") +
           "\n## Wave scaling #FFD6AB\n" + g("cp", "m1", "sn", "m2", "ab", "ad", "sc") +
           "\n## Fins #FFD6AB\n" + g("ed", "jn", "nv2", "ex", "cap") +
           "\n## Display #B2FAB4\n" + g("col", "pv") + "\n##")
    c.call("gh_edit", {"ops": ops, "layout": "tidy"}, expect=[r"tidied 30", r"\+6 groups"])
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])
    c.call("view", {"target": "gh", "focus": "group:Wave*"}, expect=[r"gh canvas"])
    c.call("gh_edit", {"save": os.path.join(TMP, "tidy.gh")}, expect=[r"saved"])
    t = c.call("gh_edit", {"layout": "tidy", "relays": True}, expect=[r"relay"])
    t2 = c.call("gh_edit", {"layout": "tidy", "relays": True}, expect=[r"tidied"])
    c.check(re.search(r"\b0 relay|no new relay|relays: 0", t2) is not None or "relay" not in t2, "relays idempotent", t2.splitlines()[0])
    c.call("view", {"target": "gh"}, expect=[r"gh canvas"])
    c.call("gh_edit", {"open": os.path.join(TMP, "tidy.gh")}, expect=[r"opened|open"], forbid=("internal engine error",))
    c.call("gh_edit", {"open": os.path.join(TMP, "tidy.gh")}, ok=None, forbid=("internal engine error",), label="re-open same file")
    c.call("query", {"scope": "gh", "level": "summary"}, expect=[r"30|object"])


def c17():
    c = Case("17_roundtrip", "reverse engine: canvas → shorthand → new canvas, same result")
    m = CTX.get("tower")
    t1 = c.call("gh_edit", {"new": True, "ops": """## Inputs #DBABFF
n = Number Slider {v=6, r=2..40, int} "Floors"
w = Number Slider {v=20, r=5..40} "Width"
## Shape #ABDBFF
ser = Series (S=0, N=3.5, C=n)
pt = Construct Point (Z=ser)
pl = XY Plane (O=pt)
rec = Rectangle (P=pl, X=w, Y=w)
ang = Range (D=0..1, N=n^e(x-1))
rot = Rotate (G=rec.R, A=ang, P=pl)
lft = Loft (C=rot.G^f)
vl = Value List {items="A:0|B:1|C:2", sel=1}
ex = Expression {expr="x*2"}
tg = Boolean Toggle {v=true}
lt = Smaller Than (A=ser, B=10)
cp = Cull Pattern (L=ser, P=lt.<)
pp = Plane {set=XZ}"""}, expect=[r"ok: 15/15"])
    src = c.call("query", {"scope": "gh", "filter": "all"}, expect=[r"Loft"])
    body = "\n".join(l for l in src.splitlines() if not re.match(r"^\d+ object|^---|^\s+(=>|!|\?)", l))
    t2 = c.call("gh_edit", {"new": True, "ops": body}, expect=[r"ok: "], forbid=("ERROR", "partial", "FAILED"))
    dst = c.call("query", {"scope": "gh", "filter": "all"})
    norm = lambda s: sorted(re.sub(r"g\d+", "g", l) for l in s.splitlines() if l.strip() and not l.startswith(("#", "---")) and "object(s)" not in l)
    c.check(norm(src) == norm(dst), "round trip identical (handles normalised)",
            "" if norm(src) == norm(dst) else "diff: " + " | ".join(set(norm(src)) ^ set(norm(dst)))[:400])


def c18():
    c = Case("18_robustness", "errors are answers: bad input, timeouts, dialogs")
    c.call("nope", {}, ok=False, expect=[r"unknown tool"], forbid=())
    c.call("query", {"scope": "rhino", "levle": "list"}, ok=None, expect=[r"ignored args: levle"])
    c.call("gh_edit", {"new": True, "ops": "a = Nonexistent Component Xyz\nb = Number Slider {v=2}\nc = Circle (R=b, Q=3)\nd = Loft (C=zz)"},
           ok=None, expect=[r"partial|FAILED", r"L1", r"L3", r"L4"], forbid=("internal engine error",))
    c.call("gh_edit", {"ops": "this is ( not valid"}, ok=None, expect=[r"L1"], forbid=("internal engine error",))
    c.call("gh_edit", {"ops": "g99999 (A=1)"}, ok=None, expect=[r"g99999"], forbid=("internal engine error",))
    c.call("gh_edit", {"atomic": True, "ops": "ok1 = Number Slider {v=1}\nbad = Nope Nope"}, ok=None, expect=[r"atomic|rolled back|nothing applied|reverted"], forbid=("internal engine error",))
    c.call("run", {"code": "x = 1\ndef f(:\n  pass"}, ok=False, expect=[r"line 2", r">\s+2\| def f"], forbid=("internal engine error", "stage"))
    c.call("run", {"code": "a = 1\nb = a / 0"}, ok=False, expect=[r"ZeroDivision", r">\s+2\| b = a / 0"], forbid=("internal engine error",))
    c.call("run", {"lang": "cs", "code": "int x = \"a\";"}, ok=False, expect=[r"error|CS"], forbid=("internal engine error",))
    c.call("view", {"focus": "Z9:Q1"}, ok=None, expect=[r"cell|focus|A-H"], forbid=("internal engine error",))
    c.call("undo", {"checkpoint": "c99999"}, ok=False, expect=[r"not found"], forbid=("reverted",))
    c.call("query", {"filter": "layer:NoSuchLayer"}, ok=None, expect=[r"0 object|no object|nothing"], forbid=("internal engine error",))
    c.call("memory", {"action": "fly"}, ok=False, expect=[r"list|save_pref"], forbid=("internal engine error",))
    # timeout → late result
    t = c.call("run", {"code": "import time\ntime.sleep(6)\nprint('late done')", "timeout": 2}, ok=None, expect=[r"timeout|still running"], forbid=("internal engine error",))
    c.call("query", {"scope": "status"}, expect=[r"busy|running|idle"])
    time.sleep(6)
    c.call("query", {}, expect=[r"late done|late result"], label="late result delivered")
    # modal dialog → named, dismissed
    c.call("run", {"code": "import Rhino\nRhino.UI.Dialogs.ShowMessage('Regression dialog text', 'Nextra test')\nprint('after dialog')", "timeout": 3},
           ok=None, expect=[r"dialog|timeout"], forbid=("internal engine error",))
    c.call("query", {"scope": "status"}, expect=[r"Nextra test|Regression dialog"])
    c.call("query", {"scope": "status", "dismiss_dialog": True}, expect=[r"dismiss|closed|cancel"])
    time.sleep(2)
    c.call("query", {"scope": "status"}, expect=[r"idle|late"])


def c19():
    c = Case("19_memory", "prefs, mistakes, compact, rules cap, list")
    c.call("memory", {"action": "save_pref", "text": "units: mm for architecture"}, expect=[r"pref"])
    c.call("memory", {"action": "save_pref", "text": "units: m for structure"}, expect=[r"pref"])
    t = c.call("memory", {"action": "list"}, expect=[r"units: m for structure"])
    c.check("units: mm for architecture" not in t, "pref with same key replaced")
    c.call("memory", {"action": "log_mistake", "text": "Karamba lengths read as metres in a mm doc", "fix": "scale x0.001"}, expect=[r"logged"])
    c.call("memory", {"action": "compact"}, expect=[r"mistakes\.log"])
    c.call("memory", {"action": "set_rules", "text": "x" * 2500}, ok=False, expect=[r"2000"], forbid=())
    c.call("memory", {"action": "set_rules", "text": "- Karamba: model in metres.\n- Range gives N+1 values."}, expect=[r"rules\.md saved"])
    c.call("memory", {"action": "list"}, expect=[r"Karamba: model in metres", r"marina_bay_sands|frame_building"])


def c20():
    c = Case("20_bridge", "MCP bridge over stdio: tools/list, call, binding refusal")
    node = shutil.which("node")
    def mcp(msgs, env=None):
        p = subprocess.run([node, os.path.join(REPO, "bridge", "server", "index.js")], input="\n".join(json.dumps(m) for m in msgs) + "\n",
                           capture_output=True, text=True, encoding="utf-8", timeout=60, env=dict(os.environ, **(env or {})))
        return [json.loads(l) for l in p.stdout.splitlines() if l.strip()]
    init = {"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {"protocolVersion": "2025-06-18"}}
    r = mcp([init, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "query", "arguments": {"scope": "status"}}}])
    names = [t["name"] for t in r[1]["result"]["tools"]] if len(r) > 1 else []
    c.check(names == ["view", "query", "run", "gh_edit", "undo", "memory"], "6 tools listed", str(names))
    schema_chars = len(json.dumps(r[1]["result"]["tools"])) if len(r) > 1 else 0
    c.check(schema_chars // 4 < 1600, "schema ≤ 1.6k tokens", f"{schema_chars // 4} tok")
    txt = r[2]["result"]["content"][0]["text"] if len(r) > 2 else ""
    c.check("engine gen" in txt, "call through bridge works", txt[:80])
    r = mcp([init, {"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "query", "arguments": {}}}], env={"NEXTRA_FILE": "no_such_file_xyz.3dm"})
    txt = r[1]["result"]["content"][0]["text"] if len(r) > 1 else ""
    c.check("not running" in txt and r[1]["result"].get("isError"), "bound to missing file → refuses, never another Rhino", txt[:120])
    r = mcp([init, {"jsonrpc": "2.0", "id": 2, "method": "tools/list"}], env={"NEXTRA_MODE": "ask"})
    names = [t["name"] for t in r[1]["result"]["tools"]] if len(r) > 1 else []
    c.check(names == ["view", "query"], "ask mode = read-only tools", str(names))


def c21():
    c = Case("21_api", "API reference search: RhinoCommon, GH SDK, rhinoscriptsyntax")
    c.call("query", {"scope": "api"}, expect=[r"rc \d{4,}", r"gh \d{4,}", r"rs \d{3,}"])
    t = c.call("query", {"scope": "api", "filter": "loft"}, expect=[r"Brep\.CreateFromLoft\(IEnumerable<Curve> curves", r"→ Brep\[\]", r"rs\.AddLoftSrf"])
    c.check(len(t) < 2600, "list result compact", f"{len(t)} chars")
    t = c.call("query", {"scope": "api", "filter": "Brep.CreateFromLoft"}, expect=[r"curves: The curves to loft", r"returns:"])
    c.check(t.count("curves: The curves to loft") == 1, "overload docs not repeated")
    c.call("query", {"scope": "api", "filter": "Brep"}, expect=[r"class Brep : GeometryBase", r"props: .*IsSolid", r"CreateFromLoft\(3\)"])
    c.call("query", {"scope": "api", "filter": "Curve.ClosestPoint"}, expect=[r"out double t", r"python: out parameters"])
    c.call("query", {"scope": "api", "filter": "rs.AddLoftSrf"}, expect=[r"loft_type", r"returns:"])
    c.call("query", {"scope": "api", "filter": "src:gh GH_Document.NewSolution"}, expect=[r"NewSolution\(bool expireAllObjects"])
    c.call("query", {"scope": "api", "filter": "LoftType"}, expect=[r"Straight=3"])
    c.call("query", {"scope": "api", "filter": "Brep.CreatFromLof"}, expect=[r"did you mean: .*Brep\.CreateFromLoft"])
    c.call("query", {"scope": "api", "filter": "zzqqxx nothing"}, expect=[r"no API match"])


def c22():
    c = Case("22_user_folder", "user folder: preferences, workflows, plug-in guides, scripts")
    t = c.call("memory", {"action": "init"}, expect=[r"user folder "])
    udir = t.split("user folder ", 1)[1].strip()
    c.check(os.path.exists(os.path.join(udir, "README.md")) and os.path.exists(os.path.join(udir, "workflows", "_TEMPLATE.md")), "folder seeded with README + templates", udir)
    c.call("memory", {"action": "save_workflow", "name": "regress_facade", "when": "when the user asks for a regression facade test", "text": "1. use sliders\n2. group by purpose"}, expect=[r"saved"])
    c.call("memory", {"action": "save_workflow", "name": "regress_nowhen", "text": "x"}, ok=False, expect=[r"when"], forbid=())
    c.call("memory", {"action": "save_guide", "name": "RegressPlug", "text": "## Rules\n- regress rule one"}, expect=[r"saved"])
    t = c.call("memory", {"action": "list"}, expect=[r"your workflows", r"regress_facade: when the user asks for a regression facade test", r"RegressPlug"])
    c.call("query", {"scope": "docs", "filter": "workflows/regress_facade"}, expect=[r"group by purpose"])
    c.call("query", {"scope": "docs"}, expect=[r"workflows/regress_facade\.md \(yours\)"])
    for f in ("workflows/regress_facade.md", "plugins/RegressPlug.md"):
        try:
            os.remove(os.path.join(udir, f))
        except OSError:
            pass


def c23():
    c = Case("23_selection_safety", "exact layer/name/group matching, inside:, handles stable across GH documents")
    reset(c)
    c.call("run", {"code": """for nm in ["Beams", "Beams_old", "FE", "FE::Lines"]:
    if doc.Layers.FindByFullPath(nm, -1) < 0:
        if "::" in nm:
            ly = Rhino.DocObjects.Layer(); ly.Name = nm.split("::")[1]; ly.ParentLayerId = doc.Layers[doc.Layers.FindByFullPath("FE", -1)].Id; doc.Layers.Add(ly)
        else:
            doc.Layers.Add(nm, System.Drawing.Color.Black)
def add(pt, layer, name):
    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = doc.Layers.FindByFullPath(layer, -1); a.Name = name
    doc.Objects.AddPoint(rg.Point3d(*pt), a)
add((0,0,0), "Beams", "B1"); add((5,0,0), "Beams_old", "B10"); add((0,0,9), "FE::Lines", "L1")"""}, expect=[r"\+3|rhino delta"])
    c.call("query", {"filter": "layer:Beams"}, expect=[r"^1 object", r"\"B1\""], forbid=("B10", "ERROR"))
    c.call("query", {"filter": "layer:Beams*"}, expect=[r"^2 object"])
    c.call("query", {"filter": "name:B1"}, expect=[r"^1 object"], forbid=("B10", "ERROR"))
    c.call("query", {"filter": "layer:FE"}, expect=[r"^1 object", r"FE::Lines"])
    c.call("query", {"filter": "inside:-1,-1,-1,1,1,1"}, expect=[r"^1 object", r"\"B1\""])
    c.call("query", {"filter": "bbox:-1,-1,-1,6,1,1"}, expect=[r"^2 object"])
    # GH: groups with similar names, handles across documents
    os.makedirs(TMP, exist_ok=True)
    t = c.call("gh_edit", {"new": True, "ops": "## Inputs #DBABFF\na = Number Slider {v=1} \"A\"\n## Inputs 2 #DBABFF\nb = Number Slider {v=2} \"B\""}, expect=[r"ok: 2/2"])
    m = ids(t)
    c.call("query", {"scope": "gh", "filter": "group:Inputs"}, expect=[r"^1 object", r"\"A\""], forbid=("\"B\"", "ERROR"))
    c.call("query", {"scope": "gh", "filter": "group:Input*"}, expect=[r"^2 object"])
    pa = os.path.join(TMP, "doc_a.gh")
    c.call("gh_edit", {"save": pa}, expect=[r"saved"])
    c.call("gh_edit", {"new": True, "ops": "x = Panel {text=\"other doc\"}\ny = Number Slider {v=9} \"Y\""}, expect=[r"ok: 2/2"])
    c.call("gh_edit", {"open": pa}, expect=[r"already open|switched|opened"])
    c.call("query", {"scope": "gh", "filter": m["a"]}, expect=[r"\"A\""], forbid=("other doc", "ERROR"))
    t = c.call("gh_edit", {"ops": "py = Python 3 Script -> (o) {code=\"o = 1\"}"}, expect=[r"ok: 1/1"])
    py = ids(t)["py"]
    t = c.call("gh_edit", {"script": {"handle": py, "mirror": True}}, expect=[r"doc_a_g\d+\.py"])


V3 = os.path.join(REPO, "test", "v3", "scripts")


def v3(name):
    with open(os.path.join(V3, name), encoding="utf-8") as f:
        return f.read()


def c24():
    c = Case("24_freeform", "freeform canopy (loft + offset solid + columns), inspect, raise the crown")
    reset(c)
    t = c.call("run", {"code": v3("canopy.py"), "args": {}, "save_as": "canopy"}, expect=[r"area 479\.9 m2", r"\[Canopy\]", r"×?8|COL_N4"])
    ck = re.search(r"checkpoint (c\d+)", t).group(1)
    c.call("query", {"filter": "name:CANOPY", "level": "detail"}, expect=[r"solid", r"area", r"faces"])
    c.call("undo", {"checkpoint": ck}, expect=[r"reverted"])
    c.call("run", {"lib": "canopy", "args": {"h_mid": 8400}}, expect=[r"crown 8400/4500", r"area \d{3}\.\d m2"])
    c.call("view", {"view": "iso"}, expect=[r"Grid"])


def c25():
    c = Case("25_gh_trees", "data trees: grafted grid → rows/columns → loft; swap the graft")
    t = c.call("gh_edit", {"new": True, "ops": v3("trees.ghs")}, expect=[r"ok: 17/17", r"10 Curve 10 branches", r"1 Brep"])
    m = ids(t)
    c.call("query", {"scope": "gh", "filter": m["rows"], "level": "data", "items": 2}, expect=[r"6 Curve 6 branches"])
    c.call("gh_edit", {"ops": f"{m['pt']} (X={m['sx']}^g, Y={m['sy']}^x)"}, expect=[r"ok: 1/1", r"6 Curve 6 branches"])
    c.call("query", {"scope": "gh", "filter": m["pt"]}, expect=[r"X=g\d+\^g", r"Y=g\d+\)"], forbid=("Y=g6^g", "ERROR"))


def c26():
    c = Case("26_big_model", "large model (1,224 objects): overview, longest beam on level 4, region count, region image")
    reset(c)
    c.call("run", {"code": v3("big_model.py")}, expect=[r"1224 objects"], label="setup")
    c.call("query", {}, expect=[r"1224 objects", r"\[MEP\]: 540 polyline"])
    t = c.call("query", {"filter": "name:BX*_L4", "limit": 60}, expect=[r"12000x450x300", r"BX24_L4"])
    c.check(t.count("\n") < 45, "level-4 X-beam list compact", f"{t.count(chr(10))} lines")
    c.call("query", {"filter": "layer:MEP inside:-1,-1,0,12001,30001,7300", "level": "summary"}, expect=[r"; 60 objects"])
    c.call("view", {"view": "top", "focus": "layer:Columns"}, expect=[r"Grid"])


def c27():
    c = Case("27_karamba_shell", "Karamba shell plate: 10x6 m, 20 cm C30/37, edges supported → 2.9 cm, thicker → less")
    t = c.call("gh_edit", {"new": True, "ops": v3("kshell.ghs")}, expect=[r"ok: 23/23", r"MaxDisp"])
    m = ids(t)
    d = c.call("query", {"scope": "gh", "filter": f"{m['an']} {m['asm']}", "level": "data"}, expect=[r"Mass"])
    v = num_after(d, r"MaxDisp: .*?e\.g\. ([\d.]+)")
    mass = num_after(d, r"Mass: .*?e\.g\. ([\d.]+)")
    c.check(v is not None and 2.5 < v < 3.3, "max displacement ≈ 2.9 cm (plate theory 2.84 cm)", f"got {v}")
    c.check(mass is not None and abs(mass - 30000) < 300, "mass 30 t (10x6x0.2 m x 2500 kg/m3)", f"got {mass}")
    t2 = c.call("gh_edit", {"ops": f"{m['t']} {{v=30}}"}, expect=[r"ok: 1/1"])
    d2 = c.call("query", {"scope": "gh", "filter": m["an"], "level": "data"}, expect=[r"MaxDisp"])
    v2 = num_after(d2, r"MaxDisp: .*?e\.g\. ([\d.]+)")
    c.check(v2 is not None and v is not None and v2 < v * 0.5, "30 cm plate deflects < half (stiffness ~ t^3, weight ~ t)", f"{v} → {v2}")
    # state change on a wired component re-reads its archive: wires must come back as real wires, not proxies drawn from (0,0)
    c.call("gh_edit", {"ops": f"{m['sup']} {{$button[3]=true}}"}, expect=[r"ok: 1/1"])
    c.call("gh_edit", {"ops": f"{m['sup']} {{$button[3]=false}}"}, expect=[r"ok: 1/1", r"MaxDisp"])
    c.call("run", {"code": DANGLING}, expect=[r"dangling 0 wired 1\b"], label="no dangling wires")


def c28():
    c = Case("28_ghs_files", ".ghs = definition as shorthand text: save → open → save is identical, results equal, refs + scripts kept")
    os.makedirs(TMP, exist_ok=True)
    a, b = os.path.join(TMP, "mix_a.ghs"), os.path.join(TMP, "mix_b.ghs")
    # scripts with type hints and a docstring, value list, quoted multi-line text, expression, stored points
    c.call("gh_edit", {"new": True, "ops": script("ghs_mix.ghs")}, expect=[r"ok: 15/15", r"Timber @ 2\.75"], forbid=(" ! ",))
    c.call("gh_edit", {"save": a}, expect=[r"saved .*mix_a\.ghs \(\.ghs text: 13 objects"])
    c.call("gh_edit", {"open": a}, expect=[r"opened mix_a\.ghs \(\.ghs\): 13 objects, 3 groups, 9 wires; solved"], forbid=("failed",))
    c.call("query", {"scope": "gh", "filter": "type:Panel", "level": "data"},
           expect=[r'"Timber @ 2\.75"', r'"6\.5"', r'"18"', r'a \\"quoted\\" line\\nand a second line'])
    c.call("gh_edit", {"save": b}, expect=[r"saved"])
    la, lb = (open(x, encoding="utf-8").read().split("\n")[2:] for x in (a, b))
    c.check(la == lb, "save → open → save gives the identical file", f"{sum(1 for x, y in zip(la, lb) if x != y)} lines differ")
    txt = "\n".join(la)
    c.check('P:list:Point3d=' in txt and 'n:item:int=' in txt and '\\"""Docstring' in txt, "Python hints + escaped docstring written", txt[:300])
    # Rhino references are kept as object ids (valid in the same .3dm); Karamba components keep a normal size
    reset(c)
    c.call("run", {"code": 'li = doc.Layers.Add("GhsRef", System.Drawing.Color.Black)\nfor i in range(3):\n    a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = li\n    doc.Objects.AddLine(rg.Line(rg.Point3d(i * 1000, 0, 0), rg.Point3d(i * 1000, 0, 3000)), a)'}, expect=[r"\+3"], label="3 lines")
    c.call("gh_edit", {"new": True, "ops": 'crv = Curve {ref="layer:GhsRef"}\nln = Length (C=crv)\n' + v3("kshell.ghs")}, expect=[r"ok: 25/25"])
    k = os.path.join(TMP, "refs.ghs")
    c.call("gh_edit", {"save": k}, expect=[r"saved"], forbid=("not stored",))
    c.check(len(re.findall(r"[0-9a-f]{8}-[0-9a-f]{4}-", open(k, encoding="utf-8").read())) == 3, "3 Rhino object ids in the file")
    c.call("gh_edit", {"open": k}, expect=[r"opened refs\.ghs"], forbid=("failed",))
    c.call("query", {"scope": "gh", "filter": "type:Length", "level": "data"}, expect=[r"3 Number"])
    c.call("query", {"scope": "gh", "filter": "group:Results", "level": "data"}, expect=[r'"2\.90'])
    c.call("run", {"code": DANGLING.replace("print('dangling', bad, 'wired', sup.Params.Input[0].SourceCount)",
                                            "wide = [o.NickName for o in d.Objects if o.Attributes and o.GetType().Name != 'GH_Group' and o.Attributes.Bounds.Width > 400]\nprint('dangling', bad, 'wired', sup.Params.Input[0].SourceCount, 'wide', wide)")},
           expect=[r"dangling 0 wired 1 wide \[\]"], label="no dangling wires, no stretched components")


DANGLING = """import Grasshopper
d = Grasshopper.Instances.ActiveCanvas.Document
ins = [p for o in d.Objects for p in (list(o.Params.Input) if hasattr(o, 'Params') and hasattr(o.Params, 'Input') else [o] if hasattr(o, 'Sources') else [])]
bad = sum(1 for p in ins for s in p.Sources if s.Attributes is None or d.FindObject(s.Attributes.GetTopLevel.DocObject.InstanceGuid, True) is None)
sup = [o for o in d.Objects if o.Name.startswith('Support')][0]
print('dangling', bad, 'wired', sup.Params.Input[0].SourceCount)"""

CTX = {}
CASES = [c00, c01, c02, c03, c04, c05, c06, c07, c08, c09_10, c12, c13, c14, c15, c16, c17, c18, c19, c20, c21, c22, c23, c24, c25, c26, c27, c28]


def summary():
    lines = ["# Regression summary", "", f"run {time.strftime('%Y-%m-%d %H:%M')}", "",
             "| case | steps | failed | ~out tok |", "|---|---|---|---|"]
    by = {}
    for s in STEPS:
        b = by.setdefault(s["case"], [0, 0, 0]); b[0] += 1; b[1] += 0 if s["ok"] else 1; b[2] += s["tout"]
    for k, (n, f, t) in by.items():
        lines.append(f"| {k} | {n} | {f} | {t} |")
    fails = [s for s in STEPS if not s["ok"]]
    lines += ["", f"**{len(STEPS) - len(fails)}/{len(STEPS)} steps passed**", ""]
    for s in fails:
        lines.append(f"- {s['case']} #{s['n']} {s['tool']} {s['label']}: {'; '.join(s['probs'])} — `{s['first'][:120]}`")
    os.makedirs(OUT, exist_ok=True)
    with open(os.path.join(OUT, "SUMMARY.md"), "w", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")
    with open(os.path.join(OUT, "steps.json"), "w", encoding="utf-8") as f:
        json.dump(STEPS, f, indent=1)
    print("\n" + "\n".join(lines))
    return not fails


def fresh_memory():
    """Point the engine at a new, empty memory folder for this run (config is re-read on every call);
    returns a function that restores the previous setting."""
    cfgp = os.path.join(os.environ["APPDATA"], "Nextra", "config.json")
    with open(cfgp, encoding="utf-8-sig") as f:
        cfg = json.load(f)
    old = cfg.get("memory_dir")
    cfg["memory_dir"] = os.path.join(HERE, "_mem", time.strftime("run-%Y%m%d-%H%M%S"))
    os.makedirs(cfg["memory_dir"], exist_ok=True)
    with open(cfgp, "w", encoding="utf-8") as f:
        json.dump(cfg, f, indent=1)

    def restore():
        with open(cfgp, encoding="utf-8-sig") as f:
            c = json.load(f)
        if old is None:
            c.pop("memory_dir", None)
        else:
            c["memory_dir"] = old
        with open(cfgp, "w", encoding="utf-8") as f:
            json.dump(c, f, indent=1)
    return restore


def wait_for_rhino(sec=120):
    t0 = time.time()
    while time.time() - t0 < sec:
        try:
            nx.call("query", {"scope": "status"}, 10, log=False)
            return True
        except Exception:
            time.sleep(3)
    return False


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    only = sys.argv[1:]
    if not wait_for_rhino():
        sys.exit("Rhino with Nextra is not reachable")
    restore = fresh_memory()
    import atexit
    atexit.register(restore)
    for fn in CASES:
        if only and not any(fn.__name__[1:].startswith(o) for o in only):
            continue
        try:
            fn()
        except Exception as e:
            import traceback
            STEPS.append({"case": fn.__name__, "n": 0, "tool": "suite", "label": "crash", "ok": False, "probs": [repr(e)], "tin": 0, "tout": 0, "sec": 0, "first": traceback.format_exc()[-300:]})
            print("  [CRASH]", fn.__name__, repr(e))
    sys.exit(0 if summary() else 1)
