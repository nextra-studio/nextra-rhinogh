# Dev install: copies the host .rhp into a Yak-style package folder (Rhino loads it at startup)
# and points the host at the engine build output so the engine hot-reloads on every rebuild.
# Run while Rhino is closed (the .rhp is locked while Rhino runs).
param([switch]$Release)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$cfg = if ($Release) { "Release" } else { "Debug" }
$ver = "0.1.4"
$pkgRoot = Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\8.0\Nextra-RhinoGh"
# the package was called Nextra-zero before; two copies of the same plug-in would clash
Remove-Item (Join-Path $env:APPDATA "McNeel\Rhinoceros\packages\8.0\Nextra-zero") -Recurse -Force -ErrorAction SilentlyContinue
$pkg = Join-Path $pkgRoot $ver
New-Item -ItemType Directory -Force $pkg | Out-Null

dotnet build "$repo\src\Nextra.Host\Nextra.Host.csproj" -c $cfg -v q -nologo | Out-Null
dotnet build "$repo\src\Nextra.Engine\Nextra.Engine.csproj" -c $cfg -v q -nologo | Out-Null

Copy-Item "$repo\src\Nextra.Host\bin\$cfg\Nextra.Host.rhp" $pkg -Force
Copy-Item "$repo\src\Nextra.Engine\bin\$cfg\Nextra.Engine.dll" $pkg -Force
@"
---
name: Nextra-RhinoGh
version: $ver
authors:
- Tatsuki
description: NextraRhinoGh - 6-tool LLM bridge (view, query, run, gh_edit, undo, memory)
url: https://github.com/nextra-studio/nextra-rhinogh
keywords: [mcp, llm, grasshopper]
"@ | Set-Content -Encoding utf8 "$pkg\manifest.yml"
Set-Content -Encoding ascii "$pkgRoot\manifest.txt" $ver

# config shared by host + engine
$appdir = Join-Path $env:APPDATA "NextraRhinoGh"
New-Item -ItemType Directory -Force $appdir | Out-Null
$cfgPath = Join-Path $appdir "config.json"
$oldCfgPath = Join-Path (Join-Path $env:APPDATA "Nextra") "config.json"
if (-not (Test-Path $cfgPath) -and (Test-Path $oldCfgPath)) {
    $oldCfg = Get-Content $oldCfgPath -Raw | ConvertFrom-Json
    if ($oldCfg.engine_path -like "*Nextra.Engine.dll" -or $oldCfg.skill_dir -like "*skill*nextra") {
        Copy-Item -LiteralPath $oldCfgPath -Destination $cfgPath
    }
}
$conf = @{}
if (Test-Path $cfgPath) { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $conf[$_.Name] = $_.Value } }
$conf["engine_path"] = "$repo\src\Nextra.Engine\bin\$cfg\Nextra.Engine.dll"
$conf["skill_dir"] = "$repo\skill\nextra"
$conf["capture_copy_dir"] = "$repo\test\captures"
if (-not $conf.ContainsKey("enabled")) { $conf["enabled"] = $true }
$conf | ConvertTo-Json | Set-Content -Encoding utf8 $cfgPath
Write-Output "installed $pkg; config $cfgPath"

# keep the project-scope Claude Code skill in sync with skill/nextra (source of truth)
$sk = Join-Path $repo ".claude\skills\nextra-rhinogh"
Remove-Item $sk -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Split-Path $sk) | Out-Null
Copy-Item "$repo\skill\nextra" $sk -Recurse
