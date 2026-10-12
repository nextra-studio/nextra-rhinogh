"""rows_v3.json → tables_v3.md (per case and whole session; output / input tokens, calls, python-fallback calls)."""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
d = json.load(open(os.path.join(HERE, "rows_v3.json"), encoding="utf-8"))
F, R, C = d["fixed"], d["rows"], d["checks"]
SYS = ["nextra", "mcneel", "rhinomcp"]
NAME = {"nextra": "Nextra", "mcneel": "McNeel", "rhinomcp": "RhinoMCP"}
PY_TOOLS = {"run_python", "execute_rhinoscript_python_code"}
TITLE = {"00": "session start", "01": "Rhino frame from scratch", "02": "Rhino modify (add storey, move, undo)", "03": "GH twisting tower",
         "04": "GH modify (rectangle → circle)", "05": "Rhino slab → GH fins → bake", "06": "GH Python component (+ error, fix)",
         "07": "GH C# component", "08": "Python component with radio buttons (+ click)", "09": "save a script", "10": "run a script from a folder",
         "12": "Karamba beam (state, dropdown)", "13": "Marina Bay Sands script", "14": "house → FE lines/shells → Karamba",
         "15": "data tree via Python + wire", "16": "messy canvas → regroup + tidy", "17": "freeform canopy (+ inspect, raise crown)",
         "18": "GH data trees (+ swap graft)", "19": "1,224-object model: overview, find, count, image", "20": "Karamba shell plate (+ thicker)"}


def fixed(s):
    f = F[s]
    return f["schema"] + f["instructions"] + f["skill"]


def billed(rows, fx):
    total, ctx = 0, 0
    for r in rows:
        total += fx + ctx
        ctx += r["out"] + r["in"]
    return total + fx + ctx


out = []
P = out.append
P("## Per case (output tokens / input tokens · calls · of which Python fallback)\n")
P("| case | " + " | ".join(NAME[s] for s in SYS) + " |")
P("|---|" + "---|" * len(SYS))
cases = []
for r in R:
    if r["scenario"] not in cases: cases.append(r["scenario"])
tot = {s: {"out": 0, "in": 0, "calls": 0, "py": 0} for s in SYS}
for c in cases:
    cells = []
    for s in SYS:
        rs = [r for r in R if r["system"] == s and r["scenario"] == c]
        o, i, py = sum(r["out"] for r in rs), sum(r["in"] for r in rs), sum(1 for r in rs if r["tool"] in PY_TOOLS)
        tot[s]["out"] += o; tot[s]["in"] += i; tot[s]["calls"] += len(rs); tot[s]["py"] += py
        cells.append("—" if not rs else f"{o:,} / {i:,} · {len(rs)}" + (f" ({py} py)" if py else ""))
    P(f"| {c} {TITLE.get(c, '')} | " + " | ".join(cells) + " |")

P("\n## Whole session (all cases in one conversation)\n")
P("| | calls | of which Python fallback | output | input from results | fixed context | **total** | billed input, no cache |")
P("|---|---|---|---|---|---|---|---|")
base = None
for s in SYS:
    t = tot[s]
    total = t["out"] + t["in"] + fixed(s)
    base = base or total
    allrows = [r for r in R if r["system"] == s]
    ratio = "" if s == "nextra" else f" ({total / base:.1f}×)"
    P(f"| {NAME[s]} | {t['calls']} | {t['py']} | {t['out']:,} | {t['in']:,} | {fixed(s):,} | **{total:,}**{ratio} | {billed(allrows, fixed(s)):,} |")

P("\n## Correctness (neutral checks after each case)\n")
P("| case | " + " | ".join(NAME[s] for s in SYS) + " |")
P("|---|" + "---|" * len(SYS))
for c in [x for x in cases if x != "00"]:
    row = []
    for s in SYS:
        v = C.get(f"{c}/{s}") or (C.get(f"03/{s}", {}).get("case04") if c == "04" else None)
        row.append("—" if v is None else str(v).replace("|", "/")[:110])
    P(f"| {c} | " + " | ".join(row) + " |")

text = "\n".join(out)
open(os.path.join(HERE, "tables_v3.md"), "w", encoding="utf-8").write(text + "\n")
print(text)
