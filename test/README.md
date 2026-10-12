# Tests

| folder | what |
|---|---|
| [v1/](v1/README.md) | **first test round (2026-10-11/12):** one folder per requested case 01–16 with the images and files the LLM produced, plus the first benchmark in `v1/11_compare/`. Results table in `v1/README.md`. |
| [v2/](v2/README.md) | **detailed benchmark (2026-10-12):** office / facade / gridshell scenarios, input and output tokens counted separately, Nextra vs McNeel vs RhinoMCP. Report, tables, raw numbers and every request/reply. |
| [v3/](v3/README.md) | **all cases on three systems (2026-10-12):** v1 cases 01–16 plus canopy, data trees, a 1,224-object model and a Karamba shell. Each runs on Nextra, McNeel and RhinoMCP with neutral checks (all correct everywhere), and input/output tokens are counted per case. |
| [regress/](regress/regress.py) | **the regression suite:** every case in one Rhino session with checks (238 steps, c00–c28). Run `python test/regress/regress.py` after every change. |

Scratch (git-ignored): `test/_tmp/`, `test/captures/`, `test/calls.jsonl` (written by `tools/nx.py`).

**Full regression** ([regress/regress.py](regress/regress.py)): all cases in one Rhino session, in order, with checks — **238/238 steps pass** (2026-10-12, after v3 and the .ghs format). It also covers things added after the first round: API reference search, the user folder, selection safety, cases 17–20, dangling-wire checks and .ghs files.
