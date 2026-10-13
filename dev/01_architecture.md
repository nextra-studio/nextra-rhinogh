# 01 — Architecture

## 1. The four layers

| layer | lives in | runs as | job |
|---|---|---|---|
| **Skill** | `skill/nextra/` | Markdown read by the LLM | teaches the LLM *how* to use the tools: priorities, the shorthand grammar, recipes |
| **Bridge** | `bridge/server/index.js` | Node process started by the MCP client | MCP over stdio ↔ HTTP to one Rhino; instance binding; heartbeat |
| **Host** | `src/Nextra.Host/` → `Nextra.Host.rhp` | Rhino plug-in, loaded at Rhino start | localhost server, UI-thread dispatcher, timeouts, dialog probe, status dots, engine loader |
| **Engine** | `src/Nextra.Engine/` → `Nextra.Engine.dll` | loaded by the host, reloaded whenever the dll changes | all Rhino/GH logic: the 6 tools |

The split between host and engine is the main architectural choice. A `.rhp` cannot be replaced while Rhino runs, so the host does only what must never change: networking, threading and loading. Everything else is in the engine, which can be rebuilt and picked up on the next call.

## 2. Life of one call

Example: the LLM calls `gh_edit {ops:"c = Circle (R=5)"}`.

1. **MCP client → bridge.** The client writes a JSON-RPC `tools/call` line on stdin, and `index.js` reads it (`rl.on("line")`).
2. **Bridge chooses the Rhino.**
   - `serverInfo()` reads every `%APPDATA%/Nextra/server-<pid>.json` and keeps the ones whose pid is still alive.
   - It then applies the binding rules (§5).
3. **Bridge → host.** It sends `POST http://127.0.0.1:<port>/call`.
   - The header is `X-Nextra-Token: <token>`.
   - The body is `{tool, args, timeout}`. The default timeout is 90 s, clamped to 5–1800 s.
4. **Host server thread** (`Server.cs`) checks the token and routes the path:
   - `/health` returns the port, engine generation and busy state.
   - `/reload` forces an engine reload.
   - `/call` goes to `Dispatcher.Call`.
5. **Dispatcher** (`Dispatcher.cs`) runs the call on the Rhino UI thread (`RhinoApp.InvokeOnUiThread`), because RhinoCommon and GH are not thread-safe.
   - Only one call runs at a time. Parallel calls from the client are **queued**, not rejected.
   - It waits in **1 s slices**. In each slice it runs `DialogProbe.AutoDismiss()`, which closes only dialogs whose title is in config `auto_dismiss`.
   - **Timeout:** the call keeps running and the reply says `timeout`, naming any blocking dialog with its text. The real result is stored and attached to the *next* reply as a "late result", capped at 6000 chars.
   - `query {scope:'status'}` is answered directly on the server thread, so it works even while the UI thread is blocked. This is how the LLM finds out about a modal dialog.
6. **EngineLoader** (`EngineLoader.cs`) checks the engine dll's timestamp. If the file changed, it loads a new copy (§3) and then calls `Nextra.Engine.Entry.Call(tool, argsJson, State)`.
7. **Engine** (`Core/Entry.cs`):
   - Parses the args (`Args`) and creates a `Reply`.
   - Switches on the tool name.
   - Catches `NxError`, which becomes `ERROR: msg` plus `hint: …`, and any other exception, which becomes an "internal engine error" with a fallback hint and is logged to `%APPDATA%/Nextra/logs/`.
   - Reports unknown arguments ("ignored args: …"), so the LLM learns the real parameter names.
8. **Reply** is `{ok, text, images:[png paths]}`. The bridge reads each PNG, base64-encodes it and returns MCP `content` as text plus images. `isError` is set to `!ok`.

## 3. Hot reload and the state bag

- `EngineLoader` reads the dll **as bytes** and loads it into a fresh collectible `AssemblyLoadContext`, so the file on disk is never locked and `dotnet build` can overwrite it at any time.
- The load context's resolver **reuses assemblies Rhino has already loaded** (RhinoCommon, Grasshopper, Eto, RhinoCodePlatform). Types therefore match exactly; otherwise `Rhino.Geometry.Point3d` from two loads would be different types.
- **State that must survive a reload** lives in one dictionary owned by the host: `EngineLoader.State`, passed to every `Entry.Call`. It holds:
  - handle tables (`r`/`g` ↔ GUID)
  - the checkpoint list
  - the last code per language (for `patch`)
  - the last view (for `focus:'C3:E5'`)
  - plug-in guides already shown this session

  **Rule:** values in `State` use only BCL types (`Dictionary`, `List`, `Guid`, `string`, `byte[]`, arrays). An engine-defined class stored there would be a different type after a reload and its cast would fail.
- `State["host.dir"]` and `State["host.appdir"]` tell the engine where the plug-in and app data are.
- Host changes (anything in `src/Nextra.Host`) need a Rhino restart. Engine changes need only `python tools/build.py`.

## 4. Threads

| thread | what runs there |
|---|---|
| server thread (`Server.AcceptLoop`) | TCP accept, HTTP parse, token check, `status` / `dismiss`, waiting for the UI thread |
| Rhino UI thread | every engine call, all RhinoCommon/GH access, status dot painting |
| WinForms timers (UI thread) | status dot following the Rhino window; GH dot waiting for GH to load |

`run` code (Python/C#) executes on the UI thread inside the engine call, which is why a long script can time out. The dispatcher then hands back a late result.

## 5. Instance binding (no cross-talk between Rhino windows)

- Each Rhino writes `server-<pid>.json` with `{port, token, pid, started, file, path}`. `Server.WriteInfo()` rewrites it on document open/save, so `file` is always the current file name.
- The bridge binds a session to **one** instance in one of three ways:
  - `NEXTRA_PID` or `NEXTRA_FILE` environment variables in the MCP config, or
  - `instance:"house.3dm"` (or a pid) on any call, which stays bound for the rest of the session.
- How the bridge resolves the binding:

  | situation | what the bridge does |
  |---|---|
  | one instance, nothing bound | uses that instance |
  | several instances, nothing bound | refuses and lists them |
  | bound instance is gone | refuses and lists the open ones; never falls back to another Rhino |
  | bound file name matches several instances | asks for a pid |

- `query {scope:'instances'}` is answered by the bridge itself and shows which instance the session is bound to.
- **Headless guard.** `HostPlugIn.OnLoad` does not start the server inside headless hosts. Rhino.Compute and Hops' `compute.geometry.exe` load plug-ins too, and once overwrote the server file.

## 6. Status dots and commands

- **Rhino dot** (`StatusDot.cs`) is a tiny borderless WinForms form owned by the Rhino main window.
  - It sits just inside the bottom-right corner (near the status bar) and follows the window.
  - It can be dragged; the offset is remembered.
  - It is TopMost only while Rhino or the GH editor is the foreground app, so it never covers other programs.
  - `AutoScaleMode.None` stops DPI scaling from blowing it up.
- **GH dot** (`GhDot.cs`) is painted on the GH canvas, bottom-right, via the canvas paint event. A timer waits until GH is loaded, so no Grasshopper type is touched earlier.
- **Colours** (`StatusDot.CurrentState`):
  - green = a client called within the last 90 s (the bridge sends a `status` heartbeat every 45 s)
  - amber = server up, no client
  - red = server down
- **Click:** red restarts the server, trying the next free port; any other colour shows the status text.
- **Commands** (`HostPlugIn.cs`):

  | command | effect |
  |---|---|
  | `Nextra` | status |
  | `NextraOn` | start the server, show both dots, persist `enabled:true` |
  | `NextraOff` | stop the server, remove both dots, persist `enabled:false` |

## 7. Files outside the repo (runtime)

`%APPDATA%/Nextra/`:

| path | written by | content |
|---|---|---|
| `server-<pid>.json` | host | port, token, pid, start time, open file |
| `config.json` | user, deploy script, host | `port`, `enabled`, `engine_path`, `skill_dir`, `plugin_docs`, `library_dirs`, `memory_dir`, `auto_dismiss`, `capture_copy_dir` |
| `user/preferences.md`, `workflows/`, `plugins/`, `scripts/` | engine (`UserDocs.cs`) | persistent user instructions, outside the plug-in package |
| `memory/rules.md`, `mistakes.log`, `library/`, `plugins/` | engine (`Memory.cs`) | learned rules, error log, scripts, and legacy guides (see 05 §9) |
| `captures/*.png` | engine | images returned by `view` (also copied to `capture_copy_dir` in dev) |
| `logs/engine-YYYYMMDD.log` | engine | internal errors with full stack traces (never sent to the LLM) |
| `dot.txt` | host | remembered dot offset |

Next to an open `.3dm`, in `<folder>/.nextra/`:
- `scripts/gN.py` — script-component mirrors
- `library/` — project scripts

A `scripts/` folder next to the `.3dm` is also indexed as library.
