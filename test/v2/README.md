# Benchmark v2 — Nextra vs McNeel Rhino-MCP-Platform vs RhinoMCP

Detailed scenarios, input and output tokens counted separately, run live in the same Rhino (2026-10-12).
**Read first: [REPORT.md](REPORT.md)** (method, results, what went wrong, capability table, caveats).

| file | what |
|---|---|
| `REPORT.md` | the report |
| `tables_v2.md` | all result tables, generated from the raw rows |
| `rows_v2.json` | raw numbers: every call (system, scenario, task, tool, output tokens, input tokens), fixed context per system, correctness checks |
| `out/<A\|B\|C>/<system>/NN_task_tool.txt` | every request and reply, exactly as exchanged (A = Rhino office, B = GH facade, C = GH gridshell) |
| `bench_v2.py` | the live harness (needs McNeel on :5123 and RhinoMCP, see `start_servers.py`) |
| `ghspec.py` | the neutral Grasshopper specs and the per-system emitters (shorthand / McNeel JSON / RhinoMCP JSON) |
| `office.py` | the office model script used in scenario A |
| `resolve_comps.py`, `start_servers.py` | setup helpers run inside Rhino |
| `undo_probe.py` | diagnostic: does other tools' activity disturb Rhino's undo? |
| `gen_report.py` | `rows_v2.json` → `tables_v2.md` |
| `run_all.log`, `run_C.log` | console output of the last runs |

Run: start Rhino with Nextra, install RhinoMCP with Yak (and uninstall it afterwards), `python test/v2/start_servers.py` via `nx.py run --code`, then `python test/v2/bench_v2.py [A] [B] [C]` and `python test/v2/gen_report.py`.

The first, simpler benchmark (v1: 36-object frame, 16-node tower) lives in [../v1/11_compare/](../v1/11_compare/).
