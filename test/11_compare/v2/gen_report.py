"""rows_v2.json → tables for REPORT (markdown on stdout and in tables_v2.md)."""
import json, os

HERE = os.path.dirname(os.path.abspath(__file__))
d = json.load(open(os.path.join(HERE, "rows_v2.json"), encoding="utf-8"))
F, R, C = d["fixed"], d["rows"], d["checks"]
SYS = ["nextra", "mcneel", "rhinomcp"]
NAME = {"nextra": "Nextra", "mcneel": "McNeel Rhino-MCP-Platform", "rhinomcp": "RhinoMCP"}
SCEN = {"A": "A · Rhino: 4-storey office (548 objects) + add storey, move core, delete facade side, undo",
        "B": "B · GH: attractor facade (27 components, 5 groups) + attractor→curve, opening scale slider, tidy",
        "C": "C · GH: organic gridshell (27 components) + parameter change, edge beams"}


def fixed(s):
    f = F[s]
    return f["schema"] + f["instructions"] + f["skill"]


def billed(rows, fx):
    """Input tokens billed without prompt caching: every request re-sends the fixed context plus everything so far
    (calls 1..N and one final answer turn)."""
    total, ctx = 0, 0
    for r in rows:
        total += fx + ctx
        ctx += r["out"] + r["in"]
    return total + fx + ctx


def cached(rows, fx):
    """Same with prompt caching: a request costs 10% for the prefix already sent before (cache read) and 100% for
    what is new since the previous request (the last call + its result). Cache-write surcharge ignored."""
    total, seen, ctx = float(fx), fx, fx          # request 1: fixed context at full price
    for r in rows:
        ctx += r["out"] + r["in"]                 # next request = everything so far
        total += 0.1 * seen + (ctx - seen)
        seen = ctx
    return int(total)


out = []
P = out.append
P("## Fixed context (in every request)\n")
P("| | tool schemas | server instructions | skill (loaded once) | total |")
P("|---|---|---|---|---|")
for s in SYS:
    f = F[s]
    P(f"| {NAME[s]} | {f['schema']:,} | {f['instructions']:,} | {f['skill']:,} | **{fixed(s):,}** |")

grand = {s: {"calls": 0, "out": 0, "in": 0} for s in SYS}
for sc in ["A", "B", "C"]:
    rows = {s: [r for r in R if r["system"] == s and r["scenario"] == sc] for s in SYS}
    if not any(rows.values()):
        continue
    P(f"\n## {SCEN[sc]}\n")
    P("| | calls | output tokens (model writes) | input tokens from results (model reads) | + fixed context | **total** | billed input, no cache | billed input, prompt cache |")
    P("|---|---|---|---|---|---|---|---|")
    for s in SYS:
        rs = rows[s]
        o, i = sum(r["out"] for r in rs), sum(r["in"] for r in rs)
        grand[s]["calls"] += len(rs); grand[s]["out"] += o; grand[s]["in"] += i
        P(f"| {NAME[s]} | {len(rs)} | {o:,} | {i:,} | {fixed(s):,} | **{o + i + fixed(s):,}** | {billed(rs, fixed(s)):,} | {cached(rs, fixed(s)):,} |")
    # per task
    tasks = []
    for s in SYS:
        for r in rows[s]:
            if r["task"] not in tasks: tasks.append(r["task"])
    P("\nper task — out / in (calls):\n")
    P("| task | " + " | ".join(NAME[s] for s in SYS) + " |")
    P("|---|" + "---|" * len(SYS))
    for t in tasks:
        cells = []
        for s in SYS:
            rs = [r for r in rows[s] if r["task"] == t]
            cells.append("—" if not rs else f"{sum(r['out'] for r in rs):,} / {sum(r['in'] for r in rs):,} ({len(rs)})")
        P(f"| {t} | " + " | ".join(cells) + " |")
    P("\ncorrectness (neutral check after the tasks):\n")
    for s in SYS:
        P(f"- {NAME[s]}: {C.get(f'{sc}/{s}')}")

P("\n## Whole session (A + B + C in one conversation)\n")
P("| | calls | output | input from results | fixed | total | billed input, no cache |")
P("|---|---|---|---|---|---|---|")
for s in SYS:
    allrows = [r for r in R if r["system"] == s]
    g = grand[s]
    P(f"| {NAME[s]} | {g['calls']} | {g['out']:,} | {g['in']:,} | {fixed(s):,} | **{g['out'] + g['in'] + fixed(s):,}** | {billed(allrows, fixed(s)):,} |")

text = "\n".join(out)
open(os.path.join(HERE, "tables_v2.md"), "w", encoding="utf-8").write(text + "\n")
print(text)
