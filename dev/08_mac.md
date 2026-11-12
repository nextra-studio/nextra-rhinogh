# 08 — Mac version

Status (2026-11-12): **beta, published.** Yak `Nextra-RhinoGh 0.1.8` has a `rh8_0-mac` distribution next to the
Windows ones. It builds and passes the automated checks below, but **nobody has run it on a real Mac yet**. The first
real-Mac report decides what to fix (see §8).

## 1. One plug-in, two `.rhp` builds

```
                 same source code (src/)
                /                       \
  Windows build (src/*.csproj)       Mac build (mac/*.csproj, branch `mac`)
  net8.0-windows, WinForms/WPF       net7.0, no UI framework
  Nextra.Host.rhp + Nextra.Engine    Nextra.Host.rhp + Nextra.Engine
  -> Nextra-RhinoGh-<v>-rh8_0-win.yak  -> Nextra-RhinoGh-<v>-rh8_0-mac.yak
                \                       /
           Yak: ONE package "Nextra-RhinoGh", one version, two distributions
```

- Same package name, version, plug-in GUID and commands. To Rhino, PackageManager and users, it is one plug-in.
- Rhino downloads only the distribution for its own system (the `-win` / `-mac` tag). A Windows user never gets the
  Mac `.rhp`.
- There is no translation layer. Rhino for Mac runs .NET, so the same C# is compiled a second time for "any OS".
  - `net7.0`, not `net8.0`: it runs on Rhino Mac with the .NET 7 runtime and with the .NET 8 runtime.
- Why two builds and not one: the Windows host uses WinForms, WPF and user32 (the status dot, dialog reading), which
  cannot load on a Mac.

## 2. Where the code lives

| what | branch | notes |
|---|---|---|
| plug-in code shared by both | `main` and `mac` (`src/`) | `mac` adds 3 `#if NEXTRA_MAC` blocks in `src/Nextra.Host/Server.cs` + `HostPlugIn.cs` |
| Mac build overlay `mac/` | **`mac` only** | projects, Mac stand-ins, tests, packaging, `mac/README.md` |
| bridge Mac path fix | `main` and `mac` | `bridge/server/index.js` + `plugin/nextra-rhinogh/server/index.js` |
| `NextraRhinoGh-Setup.command` (Mac Node.js setup) | `main` and `mac` | Mac counterpart of `NextraRhinoGh-Setup.bat` |
| README install docs (Windows + Mac) | `main` and `mac` | the "On a Mac (beta)" section |

`NEXTRA_MAC` is defined only in `mac/Directory.Build.props`. The Windows compiler never sees those blocks.
`mac/tests/windows_unchanged.ps1` proves this: it builds the Windows host and engine from `main` and from the branch,
and the bytes are identical.

### mac/ folder (branch `mac`)

| path | what |
|---|---|
| `mac/Directory.Build.props` | net7.0, `NEXTRA_MAC`, output `mac/out/<Config>/`, `RhinoCodeDll` path (Windows default `C:\Program Files\Rhino 8\System\Rhino.Runtime.Code.dll`; Mac/Linux: pass `-p:RhinoCodeDll=…` or put it in `mac/refs/`) |
| `mac/Nextra.Host.Mac/Nextra.Host.Mac.csproj` | compiles `src/Nextra.Host/*.cs` **except** `StatusDot.cs`, `GhDot.cs`, `DialogProbe.cs`; RhinoCommon NuGet 8.0.23304 (→ Yak tag `rh8_0`) |
| `mac/Nextra.Host.Mac/MacPlatform.cs` | Mac stand-ins with the same class names: `StatusDot` (prints the state on the command line), `GhDotHook` (no-op), `DialogProbe` (reports nothing), `MacShell` (clipboard `pbcopy`, Finder `open`) |
| `mac/Nextra.Engine.Mac/Nextra.Engine.Mac.csproj` | compiles **all** `src/Nextra.Engine/**/*.cs` unchanged. RhinoCommon + Grasshopper NuGet 8.36.26296.17001 (keep equal to the Windows engine's Rhino). WinForms/Drawing from `Microsoft.WindowsDesktop.App.Ref 7.0.20` are **compile-only** |
| `mac/tools/PortScan/` | metadata checker: `refs` lists assembly refs + P/Invokes, `check` fails on Windows-only ones |
| `mac/tests/bridge_mac.js` (+ `as_darwin.js`) | runs the real bridge as macOS (fake HOME, fake Rhino host, MCP over stdio). Also checks that the Windows `APPDATA` path still works |
| `mac/tests/linux_build.sh` | builds the Mac projects in `mcr.microsoft.com/dotnet/sdk:8.0` (Linux, no Windows SDK) + PortScan |
| `mac/tests/windows_unchanged.ps1` | byte-identity of the Windows build vs `main` (`-Base <ref>`) |
| `mac/package_mac.ps1` | builds, scans, stages (same payload as `tools/package.ps1`) → `yak build --platform mac` → `mac/dist/` |
| `mac/README.md` | proven vs. unproven, a 10-minute smoke test on a real Mac |

## 3. What differs on a Mac

| feature | Windows | Mac |
|---|---|---|
| status dot over Rhino (`StatusDot.cs`) | WinForms window + user32 | none: `NextraRhinoGh: ready on 127.0.0.1:<port>` printed at start; `NextraRhinoGh` shows the state |
| status dot on the GH canvas (`GhDot.cs`) | yes | none yet |
| dialog detection / `dismiss_dialog` (`DialogProbe.cs`) | user32 + UI Automation | not available: a blocked Rhino is a plain timeout; dismiss says "answer the dialog in Rhino" |
| `CopyBridgePath` clipboard | WPF `Clipboard` | `/usr/bin/pbcopy` |
| `NextraRhinoGhFolder` | `explorer.exe` | `/usr/bin/open` |
| data folder (`Server.AppDir`) | `%APPDATA%\NextraRhinoGh` | `~/Library/Application Support/NextraRhinoGh` (fixed path, see §4) |
| everything else (server, token, `server-<pid>.json`, engine hot reload, 6 tools) | same code | same code |

## 4. The data-folder trap (why the path is fixed)

On macOS, .NET 7 maps `SpecialFolder.ApplicationData` to `~/.config`, but .NET 8 maps it to
`~/Library/Application Support` (a .NET 8 breaking change). The old bridge looked in `~/.config` when `APPDATA` was
unset, so a Mac Rhino on .NET 8 and the bridge would have looked in different folders: "no Rhino running".

Fix:
- **Host (Mac build):** `Server.AppDir` is always `~/Library/Application Support/NextraRhinoGh`, whatever the runtime.
- **Bridge:** `APPDATA` set → `%APPDATA%\NextraRhinoGh` (Windows, unchanged). Otherwise macOS → `~/Library/Application Support`,
  and other systems → `~/.config`.
- The engine gets the folder from the host (`host.appdir`), so it follows automatically.

## 5. Evidence (what is verified without a Mac)

| check | result |
|---|---|
| Mac projects build on Windows and on Linux (Docker, no Windows SDK) | 0 errors (Linux: 0 warnings) |
| PortScan on the Mac outputs | no P/Invokes at all. The host refs only BCL + RhinoCommon. The engine refs `System.Windows.Forms 7.0.0.0` + `System.Drawing.Common 7.0.0.0`, the same identities the cross-platform RhinoCommon (net7.0) references, so Rhino Mac supplies them |
| bridge as macOS, end to end (Windows Node 24, Linux Node 20) | finds `server-<pid>.json`, relays calls with the token |
| Windows build vs `main` | byte-identical. Negative control `-Base 116db1b` correctly reports DIFFERENT |
| `NextraRhinoGh-Setup.command` (curl from GitHub \| bash, dry run) | no Node → finds the current LTS `.pkg`; Node 20 → nothing to do; Node 16 → too old, installs |

Not useful as evidence: CA1416 (Windows-only API analyzer). It stays silent even on `Microsoft.Win32.Registry`
with these reference assemblies.

## 6. Not verified yet (needs a real Mac)

1. Rhino Mac loading a plain `.rhp` from the Yak package. **Biggest risk**: old McNeel docs describe Mac `.rhp` as folder bundles.
2. `CaptureToBitmap` + PNG save, label drawing with `System.Drawing`. The labels ask for "Segoe UI", which a Mac does not have, so a fallback font is used.
3. Grasshopper menu automation (`MenuIO`, WinForms types on GH for Mac).
4. RhinoCode Python 3 / C# (`run`); `~/.rhinocode` exists on Mac too.
5. The process-name guard: the Mac executable is `Rhinoceros`, which passes `StartsWith("Rhino")`.

The smoke test is in `mac/README.md` (branch `mac`). Logs: `~/Library/Application Support/NextraRhinoGh/logs/`.

## 7. Release: always push BOTH distributions

Each new version needs the Windows and the Mac `.yak`, or Mac users stay on the old version.

1. **Windows (on `main`)**: bump the version everywhere (06_workflow, Packaging) → `powershell tools/package.ps1` →
   `yak push dist\nextra-rhinogh-<v>-rh8_0-win.yak` → GitHub release as usual.
2. **Mac (on `mac`)**:
   ```powershell
   git switch mac; git merge main            # brings src/, bridge, docs; conflicts only if main edited next to an #if block
   # bump <Version> in mac/Nextra.Host.Mac/*.csproj and mac/Nextra.Engine.Mac/*.csproj, $ver in mac/package_mac.ps1
   powershell -File mac/tests/windows_unchanged.ps1 -Base main   # must PASS
   node mac/tests/bridge_mac.js                                  # must PASS
   powershell -File mac/package_mac.ps1                          # runs PortScan, builds mac/dist/*.yak
   & "C:\Program Files\Rhino 8\System\Yak.exe" push mac\dist\nextra-rhinogh-<v>-rh8_0-mac.yak
   git push
   ```
3. Check that both are listed: `curl https://yak.rhino3d.com/versions/Nextra-RhinoGh` → the version has `platform: win` and `platform: mac`.

New engine source files are picked up automatically (wildcard). A **new host file** is also compiled on Mac. If it uses
WinForms/WPF/user32, add it to the `Exclude` list in `Nextra.Host.Mac.csproj` and give it a stand-in in `MacPlatform.cs`.
If you bump the Windows engine's Rhino, change the RhinoCommon/Grasshopper NuGet version in `Nextra.Engine.Mac.csproj`
to match.

## 8. Website and docs

- `README.md` (both branches): Windows/Mac install steps and an "On a Mac (beta)" section. Mac Node.js setup:
  `curl -fsSL https://raw.githubusercontent.com/nextra-studio/nextra-rhinogh/main/NextraRhinoGh-Setup.command | bash`.
  Keep that file on `main` at that path, or the link breaks.
- nextra.studio (private repo `nextra-studio/nextra-site`, content in Supabase `catalog_tools`), branch
  `rhinogh-mac-beta`:
  - `InstallGuide.js` shows a Windows / Mac chooser when `install.mac` exists. Mac visitors start on Mac.
  - The Mac guide text is `install.mac` in `src/content/tools/rhino-gh.js`.
  - It is stored in the database as `ai_install.mac`. The admin form and save route pass it through unchanged.
  - `scripts/update-rhinogh-mac.mjs` writes it (needs `.env.local`).
  - **Live only after** the branch is merged/deployed **and** the script has been run (as of 2026-11-12: not yet).

## 9. Ideas for later

- Mac status dot: an Eto floating window, or a Rhino status-bar pane.
- GH canvas dot on Mac: needs testing of WinForms events on GH for Mac.
- Dialog detection on Mac: macOS Accessibility API (needs a permission prompt).
- Fold the Mac build into `main` (multi-target) once it is proven on a real Mac. The `mac` branch is only a staging layer.
