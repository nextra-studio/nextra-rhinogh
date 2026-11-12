# NextraRhinoGh on Mac (branch `mac`)

The Mac build is an **overlay**: the folder `mac/` holds its own projects, which compile the shared sources in `src/`.
The Windows projects, build settings, packaging and version files are untouched.

| Part | Windows | Mac |
|---|---|---|
| Host `.rhp` | `src/Nextra.Host` (net8.0-windows, WinForms/WPF) | `mac/Nextra.Host.Mac`: same sources (net7.0) minus 3 Windows-only files |
| Engine | `src/Nextra.Engine` | `mac/Nextra.Engine.Mac`: **all** engine sources, unchanged |
| Bridge (Node) | `bridge/server/index.js` | same file: looks in `~/Library/Application Support/NextraRhinoGh` on macOS |
| Yak package | `tools/package.ps1` → `-rh8_0-win.yak` | `mac/package_mac.ps1` → `-rh8_0-mac.yak` |

## What changed in shared files

- `src/Nextra.Host/Server.cs`, `HostPlugIn.cs`: three `#if NEXTRA_MAC` blocks. The symbol is only defined in
  `mac/Directory.Build.props`, so the Windows compiler never sees them.
  - The Mac data folder is fixed to `~/Library/Application Support/NextraRhinoGh`. On macOS, .NET 7 maps
    `ApplicationData` to `~/.config` and .NET 8 maps it to `~/Library/Application Support`, so a fixed path stops
    host and bridge from looking in different places.
  - Copying to the clipboard uses `pbcopy`, and opening the folder uses `open`. On Windows these are the WPF clipboard
    and `explorer.exe`.
- `bridge/server/index.js` (+ the copy in `plugin/`): when `APPDATA` is not set and the platform is macOS, it uses the
  folder above. `APPDATA` is always set on Windows, so the Windows path expression is unchanged.

## Mac stand-ins (`mac/Nextra.Host.Mac/MacPlatform.cs`)

| Windows feature | Mac behaviour |
|---|---|
| Floating status dot (WinForms + user32) | none; the state is printed on the command line at start, and `NextraRhinoGh` shows it anytime |
| Status dot on the Grasshopper canvas | none for now |
| Dialog detection/dismiss (user32 + UI Automation) | not available: a blocked Rhino shows as a timeout, and `dismiss_dialog` asks the user to answer the dialog in Rhino |

Everything else is the same code as on Windows: the server, token, per-instance `server-<pid>.json`, engine hot
reload, and all six tools.

## Proof (what has been verified without a Mac)

| Check | How | Result |
|---|---|---|
| Builds without any Windows SDK | `mac/tests/linux_build.sh` in `mcr.microsoft.com/dotnet/sdk:8.0` (Linux) | 0 warnings, 0 errors |
| No Windows-only dependencies | `mac/tools/PortScan check`: assembly refs + P/Invokes read from metadata | PASS: no P/Invokes at all; host references only BCL + RhinoCommon |
| WinForms / Drawing the engine needs exist on Mac | engine refs `System.Windows.Forms 7.0.0.0` and `System.Drawing.Common 7.0.0.0`, the exact identities the cross-platform RhinoCommon (net7.0, the same binary Rhino Mac runs) references | match |
| Windows-only API calls (CA1416 analyzer, enabled in the Mac build) | `dotnet build` of both Mac projects | 0 warnings |
| Bridge finds a Mac Rhino and relays calls | `node mac/tests/bridge_mac.js`: real bridge process, macOS platform, fake home and fake Rhino host, MCP over stdio. Run on Windows Node 24 and Linux Node 20 (Docker) | PASS (also for the `plugin/` copy) |
| Windows bridge unchanged | same test with `APPDATA` set, plus `node test/regress/bridge_checks.js` | PASS |
| **Windows plug-in unchanged** | `mac/tests/windows_unchanged.ps1`: builds host + engine from `main` and from this branch, compares bytes | **byte-identical** |
| Mac Yak package | `mac/package_mac.ps1` | `nextra-rhinogh-0.1.8-rh8_0-mac.yak` |

## Not proven yet (needs a real Mac)

- Rhino for Mac actually loading `Nextra.Host.rhp` from the Yak package. McNeel documents Yak `--platform mac`
  packages. Their older docs describe Mac `.rhp` folder bundles, so a plain `.rhp` file is the main open question.
- `CaptureToBitmap` + PNG save, and label drawing with `System.Drawing` (Rhino Mac ships its own implementation).
  The labels ask for "Segoe UI", which a Mac does not have, so a fallback font will be used.
- Grasshopper menu automation (`MenuIO`: component context menus through WinForms types on Grasshopper for Mac).
- The RhinoCode Python 3 / C# runner (`run`): `~/.rhinocode` exists on Mac too.
- The process-name check: the Mac executable is `Rhinoceros`, which starts with "Rhino" as the check expects.

## 10-minute smoke test on a Mac

1. Copy `nextra-rhinogh-0.1.8-rh8_0-mac.yak` to the Mac. In Rhino 8, run `_PackageManager` and install from file,
   or run `"/Applications/Rhino 8.app/Contents/Resources/bin/yak" install <file>`. Then restart Rhino.
2. The command line should say `NextraRhinoGh: ready on 127.0.0.1:8765 …`. If it doesn't, run `NextraRhinoGh` and copy
   the message.
3. `ls ~/Library/Application\ Support/NextraRhinoGh/` should show `server-<pid>.json` and `bridge/index.js`.
4. Point Claude at the bridge: `NextraRhinoGh > CopyBridgePath` puts the path on the clipboard. MCP server = `node <path>`.
5. Ask Claude, in this order:
   - `query {scope:'instances'}`
   - `view` (Rhino viewport image)
   - `run` with a 1-line Python script
   - open Grasshopper, then `gh_edit` with `n = Number Slider`
   - `view {target:'gh'}`
6. Logs: `~/Library/Application Support/NextraRhinoGh/logs/`.

## Building

```sh
# Windows (uses C:\Program Files\Rhino 8\System\Rhino.Runtime.Code.dll for compiling only)
dotnet build mac/Nextra.Host.Mac/Nextra.Host.Mac.csproj -c Release
dotnet build mac/Nextra.Engine.Mac/Nextra.Engine.Mac.csproj -c Release      # -> mac/out/Release/
powershell -File mac/package_mac.ps1                                          # -> mac/dist/*.yak

# Mac / Linux: pass a Rhino.Runtime.Code.dll (from a Rhino 8 install), or put one in mac/refs/
dotnet build mac/Nextra.Engine.Mac/Nextra.Engine.Mac.csproj -c Release -p:RhinoCodeDll=/path/to/Rhino.Runtime.Code.dll

# checks
node mac/tests/bridge_mac.js
dotnet run --project mac/tools/PortScan -- check mac/out/Release/Nextra.Host.rhp mac/out/Release/Nextra.Engine.dll
powershell -File mac/tests/windows_unchanged.ps1
```
