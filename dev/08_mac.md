# 08 — Mac version

Status (2026-11-13): **beta, published.** Yak `Nextra-RhinoGh 0.1.9` (Eto status dot, icon) has a `rh8_0-mac` distribution next to the
Windows ones. It builds and passes the automated checks below, but **nobody has run it on a real Mac yet**. The first
real-Mac report decides what to fix (see §8).

## 1. One plug-in, two `.rhp` builds

```
                 same source code (src/)
                /                       \
  Windows build (src/*.csproj)       Mac build (mac/*.csproj)
  net8.0-windows                     net7.0
  UI: Eto (+ Windows dialog probe)   UI: Eto (same code)
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
- Why two builds and not one: two Windows-only parts remain on Windows: reading Rhino's dialogs (`DialogProbe.cs`,
  UI Automation + user32) and one Eto.Wpf workaround (`WindowsQuirks.cs`, DWM). Everything visible to the user (status
  dot, Grasshopper canvas dot, clipboard) is the same Eto code on both systems (since the `eto-ui` change, §10).

## 2. Where the code lives

Everything is on `main`; it was merged in on 2026-11-12. The `mac` branch was the staging layer and is kept only for
history; do new work on `main`.

| what | where | notes |
|---|---|---|
| plug-in code shared by both | `src/` | 3 `#if NEXTRA_MAC` blocks in `src/Nextra.Host/Server.cs` + `HostPlugIn.cs` |
| Mac build overlay | `mac/` | projects, Mac stand-ins, tests, packaging, `mac/README.md` |
| bridge Mac path fix | `bridge/server/index.js` + `plugin/nextra-rhinogh/server/index.js` | one file for both systems |
| Mac Node.js setup | `NextraRhinoGh-Setup.command` | Mac counterpart of `NextraRhinoGh-Setup.bat` |
| install docs (Windows + Mac) | `README.md` | the "On a Mac (beta)" section |

`NEXTRA_MAC` is defined only in `mac/Directory.Build.props`. The Windows compiler never sees those blocks.
`mac/tests/windows_unchanged.ps1 -Base <ref>` proves this: it builds the Windows host and engine from `<ref>` and from
the current checkout, and the bytes are identical. It was run before and after the merge into `main`.

### mac/ folder

| path | what |
|---|---|
| `mac/Directory.Build.props` | net7.0, `NEXTRA_MAC`, output `mac/out/<Config>/`, `RhinoCodeDll` path (Windows default `C:\Program Files\Rhino 8\System\Rhino.Runtime.Code.dll`; Mac/Linux: pass `-p:RhinoCodeDll=…` or put it in `mac/refs/`) |
| `mac/Nextra.Host.Mac/Nextra.Host.Mac.csproj` | compiles `src/Nextra.Host/*.cs` **except** `DialogProbe.cs` and `WindowsQuirks.cs`; RhinoCommon + Grasshopper NuGet 8.0.23304 (→ Yak tag `rh8_0`; brings Eto 2.7 + Rhino.UI, the same Eto the Windows host compiles against) |
| `mac/Nextra.Host.Mac/MacPlatform.cs` | the only Mac stand-ins: `DialogProbe` (reports nothing), `PlatformQuirks.AfterShow` (no-op), `MacShell.Reveal` (Finder `open`) |
| `mac/Nextra.Engine.Mac/Nextra.Engine.Mac.csproj` | compiles **all** `src/Nextra.Engine/**/*.cs` unchanged. RhinoCommon + Grasshopper NuGet 8.36.26296.17001 (keep equal to the Windows engine's Rhino) |
| `mac/GrasshopperApiRefs.props` | imported by both Mac projects: Grasshopper's API types (WinForms canvas/menus, System.Drawing) from `Microsoft.WindowsDesktop.App.Ref 7.0.20`, **compile-only** |
| `mac/tools/PortScan/` | metadata checker: `refs` lists assembly refs + P/Invokes, `check` fails on Windows-only ones |
| `mac/tests/bridge_mac.js` (+ `as_darwin.js`) | runs the real bridge as macOS (fake HOME, fake Rhino host, MCP over stdio). Also checks that the Windows `APPDATA` path still works |
| `mac/tests/linux_build.sh` | builds the Mac projects in `mcr.microsoft.com/dotnet/sdk:8.0` (Linux, no Windows SDK) + PortScan |
| `mac/tests/windows_unchanged.ps1` | byte-identity of the Windows build vs `main` (`-Base <ref>`) |
| `mac/package_mac.ps1` | builds, scans, stages (same payload as `tools/package.ps1`) → `yak build --platform mac` → `mac/dist/` |
| `mac/README.md` | proven vs. unproven, a 10-minute smoke test on a real Mac |

## 3. What differs on a Mac

| feature | Windows | Mac |
|---|---|---|
| status dot over Rhino (`StatusDot.cs`) | **same Eto code** | **same Eto code** (not yet seen on a real Mac) |
| status dot on the GH canvas (`GhDot.cs`) | **same code** (Grasshopper canvas API, Eto timer) | **same code** (not yet seen on a real Mac) |
| `CopyBridgePath` clipboard | **Eto** `Clipboard` | **Eto** `Clipboard` |
| dialog detection / `dismiss_dialog` (`DialogProbe.cs`) | user32 + UI Automation | not available: a blocked Rhino is a plain timeout; dismiss says "answer the dialog in Rhino" (deliberate exception: no UI library can read another window's contents) |
| Eto.Wpf blur-behind on transparent windows (`WindowsQuirks.cs`) | switched off after the dot is shown | not needed |
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
| PortScan on the Mac outputs | no P/Invokes at all. Host and engine reference RhinoCommon, Grasshopper, Eto 2.7 / Rhino.UI (host) and `System.Windows.Forms 7.0.0.0` + `System.Drawing.Common 7.0.0.0` (Grasshopper's API), the same identities the cross-platform RhinoCommon (net7.0) references, so Rhino Mac supplies them |
| bridge as macOS, end to end (Windows Node 24, Linux Node 20) | finds `server-<pid>.json`, relays calls with the token |
| Windows build vs `main` | byte-identical up to the Eto UI change (2026-11-12; negative control `-Base 116db1b` correctly reports DIFFERENT). The Eto change (§10) changes the Windows host on purpose; it was verified live in Rhino instead |
| `NextraRhinoGh-Setup.command` (curl from GitHub \| bash, dry run) | no Node → finds the current LTS `.pkg`; Node 20 → nothing to do; Node 16 → too old, installs |

Not useful as evidence: CA1416 (Windows-only API analyzer). It stays silent even on `Microsoft.Win32.Registry`
with these reference assemblies.

## 6. Not verified yet (needs a real Mac)

1. Rhino Mac loading a plain `.rhp` from the Yak package. **Biggest risk**: old McNeel docs describe Mac `.rhp` as folder bundles.
2. `CaptureToBitmap` + PNG save, label drawing with `System.Drawing`. The labels ask for "Segoe UI", which a Mac does not have, so a fallback font is used.
3. Grasshopper menu automation (`MenuIO`, WinForms types on GH for Mac).
4. RhinoCode Python 3 / C# (`run`); `~/.rhinocode` exists on Mac too.
5. The process-name guard: the Mac executable is `Rhinoceros`, which passes `StartsWith("Rhino")`.
6. The Eto status dot on Mac: transparency, staying above Rhino, following the window. Rhino for Mac has one window
   per document: the dot follows whatever `RhinoEtoApp.MainWindow` returns (checked every 0.3 s), i.e. the active
   document's window.
7. The Grasshopper canvas dot on Mac (Grasshopper's canvas events through its WinForms API).

The smoke test is in `mac/README.md`. Logs: `~/Library/Application Support/NextraRhinoGh/logs/`.

## 7. Release: always push BOTH distributions

Each new version needs the Windows and the Mac `.yak`, or Mac users stay on the old version.

Both are built from `main`:

1. **Version**: bump it everywhere (06_workflow, Packaging) **plus** the 3 Mac spots: `<Version>` in
   `mac/Nextra.Host.Mac/Nextra.Host.Mac.csproj` and `mac/Nextra.Engine.Mac/Nextra.Engine.Mac.csproj`, and `$ver` in
   `mac/package_mac.ps1`.
2. **Windows**: `powershell tools/package.ps1` → `yak push dist\nextra-rhinogh-<v>-rh8_0-win.yak` → GitHub release as usual.
3. **Mac**:
   ```powershell
   node mac/tests/bridge_mac.js                                  # must PASS
   powershell -File mac/package_mac.ps1                          # runs PortScan, builds mac/dist/*.yak
   & "C:\Program Files\Rhino 8\System\Yak.exe" push mac\dist\nextra-rhinogh-<v>-rh8_0-mac.yak
   ```
   If you edited `src/Nextra.Host/Server.cs` or `HostPlugIn.cs` near an `#if NEXTRA_MAC` block, also build the Mac
   projects once to make sure both variants still compile.
3. Check that both are listed: `curl https://yak.rhino3d.com/versions/Nextra-RhinoGh` → the version has `platform: win` and `platform: mac`.

New engine source files are picked up automatically (wildcard). A **new host file** is also compiled on Mac. Write UI
with Eto (one code for both systems). Only when no cross-platform API exists: put the Windows part in its own file, add
it to the `Exclude` list in `Nextra.Host.Mac.csproj` and give it a stand-in in `MacPlatform.cs` (as `DialogProbe.cs`
and `WindowsQuirks.cs` do).
If you bump the Windows engine's Rhino, change the RhinoCommon/Grasshopper NuGet version in `Nextra.Engine.Mac.csproj`
to match.

## 8. Website and docs

- `README.md`: Windows/Mac install steps and an "On a Mac (beta)" section. Mac Node.js setup:
  `curl -fsSL https://raw.githubusercontent.com/nextra-studio/nextra-rhinogh/main/NextraRhinoGh-Setup.command | bash`.
  Keep that file on `main` at that path, or the link breaks.
- nextra.studio (private repo `nextra-studio/nextra-site`, content in Supabase `catalog_tools`); merged into its
  `main` on 2026-11-12:
  - `InstallGuide.js` shows a Windows / Mac chooser when `install.mac` exists. Mac visitors start on Mac.
  - The Mac guide text is `install.mac` in `src/content/tools/rhino-gh.js`.
  - It is stored in the database as `ai_install.mac`. The admin form and save route pass it through unchanged.
  - `scripts/update-rhinogh-mac.mjs` writes it (needs `.env.local`).
  - **The Mac tab appears only after that script has run once** (as of 2026-11-12: not yet). Until then the page looks
    as before. To change the Mac text later: edit `rhino-gh.js` and run the script again.

## 9. Ideas for later

- Dialog detection on Mac: macOS Accessibility API (needs a permission prompt).
- Once proven on a real Mac: one multi-targeted host project instead of `src/` + `mac/` projects.

## 10. Eto UI: one implementation for Windows and Mac (branch `eto-ui`, 2026-11-13)

User rule: Windows and Mac use the same code wherever possible. WinForms/WPF in the host were replaced by Eto.

- **Status dot** (`StatusDot.cs`): an Eto `Form`, owned by `RhinoEtoApp.MainWindow`. It is borderless and transparent,
  with `ShowActivated = false`, `Topmost` only while `Application.Instance.IsActive`, and an Eto `UITimer`. The offset is
  stored as `eto,x,y` in logical pixels; old `off,x,y` files (physical px) fall back to the default.
- **GH canvas dot** (`GhDot.cs`): the Eto `UITimer` replaced the WinForms timer. Painting still goes through
  Grasshopper's canvas API (its WinForms/System.Drawing types are Grasshopper's, the same on both systems).
- **Clipboard**: `new Eto.Forms.Clipboard().Text`.
- **Both hosts compile against the same Eto (2.7, from the RhinoCommon 8.0 package)**. The Windows host drops the
  newer local `Eto.dll` from `src/Directory.Build.props` (`<Reference Remove="Eto" />`). WPF remains only for
  `DialogProbe.cs`.
- Two Eto-on-Windows behaviours found while testing, both handled in shared code or in `WindowsQuirks.cs`:
  1. Windows enlarges a new borderless window to its minimum size (~133 x 38 logical), so an invisible area covered the
     status bar. Fix: set the size again after `Show()` (`KeepSize`, re-applied while following).
  2. Eto.Wpf turns on DWM blur-behind for every window whose background alpha or opacity is below 1
     (`WpfWindow.SetBlurBehind` → `GlassHelper.BlurBehindWindow`, found with `PortScan callers/il/field`), with no
     setting to prevent it, so a grey square appeared around the dot. Fix: `PlatformQuirks.AfterShow` switches it off
     (Windows only; no-op on Mac).
- Verified live in Rhino 8.36 on Windows: size 16 logical (20 px at 125 %); fully transparent around the dot; red when
  the server is stopped; a click restarts it (amber); green after a call; drag moves it and saves the offset; hidden
  while Rhino is minimised; focus stays on Rhino after restore; not above other apps; above the Grasshopper editor;
  GH canvas dot drawn; `CopyBridgePath` puts the path on the clipboard.
