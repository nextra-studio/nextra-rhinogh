# Mac Yak package (a second distribution of the same Nextra-RhinoGh version, next to the -win one). Output: mac/dist/.
# Builds on Windows with the Rhino 8 Yak CLI; nothing is published (yak push stays a manual step).
$ErrorActionPreference = "Stop"
$mac = $PSScriptRoot
$repo = Split-Path -Parent $mac
$ver = "0.1.9"
$dist = Join-Path $mac "dist"
$stage = Join-Path $dist "yak"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($p in "Nextra.Host.Mac", "Nextra.Engine.Mac") {
    dotnet build "$mac\$p\$p.csproj" -c Release -v q -nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$p build failed" }
}
Copy-Item "$mac\out\Release\Nextra.Host.rhp" $stage
Copy-Item "$mac\out\Release\Nextra.Engine.dll" $stage
# same payload as the Windows package (tools/package.ps1): skill docs without plugins/, and the bridge
Copy-Item "$repo\skill\nextra" (Join-Path $stage "skill") -Recurse
Remove-Item (Join-Path $stage "skill\plugins") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$repo\bridge" (Join-Path $stage "bridge") -Recurse
# Package Manager icon (Nextra N; made by tools/make_icon.py)
Copy-Item "$repo\docs\img\icon.png" (Join-Path $stage "icon.png")
@"
---
name: Nextra-RhinoGh
version: $ver
authors:
- Tatsuki
description: NextraRhinoGh for Rhino 8 + Grasshopper. Six token-lean tools (view, query, run, gh_edit, undo, memory) for any MCP client.
url: https://nextra.studio/
icon: icon.png
keywords: [mcp, llm, ai, grasshopper]
"@ | Set-Content -Encoding utf8 (Join-Path $stage "manifest.yml")

# the portability scan must pass before anything is packaged
dotnet run --project "$mac\tools\PortScan" -v q -- check "$stage\Nextra.Host.rhp" "$stage\Nextra.Engine.dll"
if ($LASTEXITCODE -ne 0) { throw "Portability scan failed" }

$yak = "C:\Program Files\Rhino 8\System\Yak.exe"
Push-Location $stage
$ErrorActionPreference = "Continue"
& $yak build --platform mac 2>&1 | Out-Host
$ErrorActionPreference = "Stop"
Pop-Location
Get-ChildItem $stage -Filter *.yak | Move-Item -Destination $dist -Force
Get-ChildItem $dist -File | Select Name, Length
