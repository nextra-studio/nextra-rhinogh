# Release packaging: Yak package (Rhino) + .mcpb (Claude Desktop). Output in dist/.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$ver = "0.1.0"
$dist = Join-Path $repo "dist"
$stage = Join-Path $dist "yak"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet build "$repo\src\Nextra.Host\Nextra.Host.csproj" -c Release -v q -nologo | Out-Null
dotnet build "$repo\src\Nextra.Engine\Nextra.Engine.csproj" -c Release -v q -nologo | Out-Null
Copy-Item "$repo\src\Nextra.Host\bin\Release\Nextra.Host.rhp" $stage
Copy-Item "$repo\src\Nextra.Engine\bin\Release\Nextra.Engine.dll" $stage
# skill docs travel with the plug-in so `query {scope:'docs'}` works for any LLM (engine looks in %APPDATA%/Nextra/skill or skill_dir)
Copy-Item "$repo\skill\nextra" (Join-Path $stage "skill") -Recurse
Copy-Item "$repo\bridge" (Join-Path $stage "bridge") -Recurse
# plug-in guides (Karamba3D.md, _TEMPLATE.md …) ship with the Rhino plug-in, not with the skill: the engine attaches their
# rules and seeds the user's folder (%APPDATA%\Nextra\user\plugins) from the template at first start
New-Item -ItemType Directory -Force (Join-Path $stage "guides") | Out-Null
Copy-Item "$repo\guides\plugins" (Join-Path $stage "guides\plugins") -Recurse
@"
---
name: Nextra-zero
version: $ver
authors:
- Tatsuki
description: Nextra for Rhino 8 + Grasshopper. Six token-lean tools (view, query, run, gh_edit, undo, memory) for any MCP client.
url: https://github.com/Tatsuki0123/nextra-rhinogh
keywords: [mcp, llm, ai, grasshopper]
"@ | Set-Content -Encoding utf8 (Join-Path $stage "manifest.yml")

$yak = "C:\Program Files\Rhino 8\System\Yak.exe"
Push-Location $stage
$ErrorActionPreference = "Continue"
& $yak build --platform win 2>&1 | Out-Host
$ErrorActionPreference = "Stop"
Pop-Location
Get-ChildItem $stage -Filter *.yak | Move-Item -Destination $dist -Force

# skill .zip for claude.ai "Upload a skill" (a folder with SKILL.md at its top). User-specific instructions do not go
# in here: they live in the user's Nextra folder and reach the model through the tools, so the uploaded skill stays generic.
python "$repo\tools\make_skill_zip.py"

# .mcpb = zip of bridge (manifest.json + server/)
$mcpb = Join-Path $dist "nextra-rhinogh-$ver.mcpb"
Remove-Item $mcpb -ErrorAction SilentlyContinue
Compress-Archive -Path "$repo\bridge\manifest.json", "$repo\bridge\server" -DestinationPath ($mcpb + ".zip")
Move-Item ($mcpb + ".zip") $mcpb -Force
Get-ChildItem $dist -File | Select Name, Length
