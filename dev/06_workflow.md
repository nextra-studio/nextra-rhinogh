# 06 — Development workflow

## Requirements
- Windows, Rhino 8 (SR35 tested), .NET 8 SDK, Python 3, Node ≥ 18.
- Optional: Karamba3D for the structural cases (the trial is limited to 50 shells).

## First setup (once)
```powershell
# Rhino must be CLOSED (the .rhp is locked while it runs)
powershell tools/deploy_dev.ps1
```
This does four things:
- builds both projects;
- copies the `.rhp` and the engine into `%APPDATA%/McNeel/Rhinoceros/packages/8.0/Nextra-RhinoGh/0.1.1/`;
- writes `config.json` with `engine_path` pointing at `src/Nextra.Engine/bin/Debug/Nextra.Engine.dll` (this enables hot reload) and `skill_dir` pointing at the repo's `skill/nextra`;
- re-syncs `.claude/skills/nextra-rhinogh`.

Start Rhino. The dot in the bottom-right corner turns amber.

## Daily loop
```bash
# edit src/Nextra.Engine/...
python tools/build.py                       # compact errors; OK → next call uses the new engine
python tools/nx.py query '{}'               # try it
python tools/nx.py gh_edit '{"ops":"c = Circle (R=5)"}'
python tools/nx.py run '{}' --code test/_tmp/x.py   # long code from a file
python tools/nx.py query '{"scope":"status"}'
python tools/nx.py health                  # engine generation, busy state
```
- **Engine change:** `build.py` is enough. The host notices the new dll timestamp on the next call. `query {scope:'status'}` shows `engine gen N`.
- **Host change** (`src/Nextra.Host`): close Rhino, run `deploy_dev.ps1`, start Rhino.
- **Skill doc change:** takes effect immediately for `query {scope:'docs'}`. Re-run `deploy_dev.ps1` (or copy the folder) to update the Claude Code skill copy.
- **Bridge change:** restart the MCP client session. `nx.py` bypasses the bridge entirely (it talks HTTP directly), so test the bridge through a real client.
- `nx.py` appends every call to `test/calls.jsonl` with token estimates (text ≈ chars/4, image ≈ w·h/750). That file is git-ignored and was the source for the benchmark numbers.
- Several Rhinos open: set `NEXTRA_FILE=house.3dm` or `NEXTRA_PID` for `nx.py` as well.

## Regression suite (run after every change)
```bash
python test/regress/regress.py            # all cases, ~5 min, needs a running Rhino with Nextra
python test/regress/regress.py 03 04      # only cases whose id starts with these (some depend on earlier ones: 04 needs 03)
```
- Runs every case in **one** Rhino session, in order, and checks each reply (regexes on the text, numeric checks like the Karamba deflection 47.04 cm and house mass ≈ 99.5 t, round-trip identity, bridge behaviour).
- Each run points the engine at a fresh memory folder (`test/regress/_mem/run-…`) and restores the config afterwards.
- Output: `test/regress/out/<case>/NN_tool.txt` (request + reply) and images, `out/SUMMARY.md`, `out/steps.json`.
- A failing step prints the first lines of the reply. Fix, rebuild, rerun the whole suite — not only the failed case — and run it twice in the same session to catch state leaks.
- Add a case for every bug fixed: a function `cNN()` using `Case.call(tool, args, expect=[regex…], ok=…, forbid=…)` and `Case.check(cond, label)`, appended to `CASES`.

## Adding things

**A new argument for an existing tool**
1. Read it in the tool's `Run` (`a.Str/Int/Bool/List`). Unread args are reported as ignored, which is a quick check that it is wired.
2. Add it to the schema in `bridge/server/index.js`. Keep the description short.
3. Mention it in the skill only if the LLM needs to know when to use it.

**A new query scope or level:** `Core/QueryTool.cs` routes scopes; the Rhino levels are in the same file, the GH ones in `Gh/GhQuery.cs`.

**A new tool** (avoid if possible; 6 is deliberate, since each tool costs schema tokens every request):
1. Add a `case` in `Entry.Call`.
2. Add a class with `Run(Args, Reply)`.
3. Add a schema in `index.js`.

**A plug-in guide:** copy `skill/nextra/plugins/_TEMPLATE.md` to `<Category>.md`, where the name is the category shown by `query {scope:'components', filter:'@X'}`. Keep `## Rules` to 12 lines or fewer.

**New shorthand syntax:** see 04 §7.

## Writing good replies (house style)
- The first line is the status and counts: `ok: 3/3 lines applied (…) , checkpoint c2`.
- Then handles, then problems, then results. Nothing the LLM cannot act on.
- Errors use `throw new NxError(message, hint)`. The hint says what to do next: valid names, a different scope, a fallback to `run`.
- Cap lists (`limit`, `Fmt.Trunc`) and say how to get more (`offset`, `level:'detail'`).
- Never return GUIDs, raw stack traces or vertices by default.
- **State bag:** BCL types only (01 §3).

## Packaging
```powershell
powershell tools/package.ps1
```
Produces in `dist/`:
- `nextra-rhinogh-0.1.1-rh8_35-win.yak`: Rhino package with the `.rhp`, engine, `skill/` and `bridge/`.
- `nextra-rhinogh-0.1.1.mcpb`: the Claude Desktop bundle (bridge + manifest).

Bump the version in `deploy_dev.ps1`, `package.ps1`, `bridge/server/index.js` (`VERSION`) and `bridge/manifest.json` together.

## Debugging

| symptom | look at |
|---|---|
| "Rhino is not reachable" | Is Rhino running? Dot colour? `%APPDATA%/Nextra/server-*.json` exists and the pid is alive? `NextraOn` |
| several instances refused | expected: pass `instance:"file.3dm"` |
| `timeout` / `busy` | `query {scope:'status'}`: a dialog name and its text. The late result comes with the next call |
| internal engine error | `%APPDATA%/Nextra/logs/engine-YYYYMMDD.log` has the full stack |
| new engine not picked up | `build.py` really said OK? `config.json` `engine_path` points at the Debug build? `nx.py reload` |
| image tags wrong | captures are in `%APPDATA%/Nextra/captures` (dev copy: `test/captures`) |
| GH component looks stretched / stray grips | `Layout.RepairBounds`; run `gh_edit {layout:'tidy'}` |

## Known pitfalls (learned during the build)
- **Rhino capture:** `ViewCapture` ignored our conduit and display mode, so the code uses `RhinoView.CaptureToBitmap` at the viewport's own aspect ratio.
- **GH save:** `GH_DocumentIO.SaveQuiet` can show a modal box on a bad path. The code writes the archive directly (`GH_Archive.WriteToFile`).
- **Opening an already-open .gh** throws in GH, so the code switches to that document instead.
- **Slider values:** set the range before the value, or the value is clamped.
- **Persistent data appends** to the defaults; clear it first.
- **Karamba:** works in metres; loads need nodes; collapsible inputs break bounds (see `SetPivot`).
- **Custom attributes on Rhino 8 script components** must implement `IScriptAttribute` (`skill/nextra/api/gh-python.md`).
- **Hops / Rhino.Compute** load plug-ins headless, hence the headless guard.
- **Rhino sometimes hangs on exit** while other MCP plug-ins are active. Save first; killing the process is then safe.
- **Patching code inline in shell heredocs** breaks on `\n` / `\d` escapes. Use an editor (or the Edit tool), never shell-generated patches for C#/regex code.
- **Never call RhinoCommon from the host's server thread** (e.g. `RhinoApp.MainWindowHandle()`), and never `GetWindowText` our own windows from it: both wait for the UI thread.
- **`run` stdout is capped (2,500 chars, middle cut)**: tools that need large output from Rhino should write a file.
- **Rhino's own undo stack** can be emptied in long sessions; Nextra's `undo` uses its checkpoints and does not depend on it.
- **Screenshots for checking:** the display is 2560×1600 physical vs 2048×1280 logical, so use DPI-aware capture.

## Repo and history
- `dev/BUILD_PLAN.md` is the checklist of everything requested and its status. Update it when you add or finish an item.
- `dev/Nextra.zero_RhinoGh_Plan.md` is the original plan.
- `test/README.md` is the results table for the cases. Add a row and a folder with evidence for each new case.
