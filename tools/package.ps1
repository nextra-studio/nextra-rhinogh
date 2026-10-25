# Release packaging: Yak package (Rhino) + .mcpb (Claude Desktop). Output in dist/.
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$ver = "0.1.5"
$dist = Join-Path $repo "dist"
$stage = Join-Path $dist "yak"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null

dotnet build "$repo\src\Nextra.Host\Nextra.Host.csproj" -c Release -v q -nologo | Out-Null
dotnet build "$repo\src\Nextra.Engine\Nextra.Engine.csproj" -c Release -v q -nologo | Out-Null
Copy-Item "$repo\src\Nextra.Host\bin\Release\Nextra.Host.rhp" $stage
Copy-Item "$repo\src\Nextra.Engine\bin\Release\Nextra.Engine.dll" $stage
# skill docs travel with the plug-in so `query {scope:'docs'}` works for any LLM (engine looks in %APPDATA%/NextraRhinoGh/skill or skill_dir)
Copy-Item "$repo\skill\nextra" (Join-Path $stage "skill") -Recurse
# plug-in guides (plugins/Karamba3D.md, _TEMPLATE.md …) belong to the skill only, not to the Rhino plug-in
Remove-Item (Join-Path $stage "skill\plugins") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$repo\bridge" (Join-Path $stage "bridge") -Recurse
@"
---
name: Nextra-RhinoGh
version: $ver
authors:
- Tatsuki
description: NextraRhinoGh for Rhino 8 + Grasshopper. Six token-lean tools (view, query, run, gh_edit, undo, memory) for any MCP client.
url: https://nextra.studio/
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
# in here: they live in the user's NextraRhinoGh folder and reach the model through the tools, so the uploaded skill stays generic.
python "$repo\tools\make_skill_zip.py"

# plug-in folder for Claude Code + Codex / ChatGPT desktop (bridge + skill in one install)
python "$repo\tools\make_plugin.py"
if ($LASTEXITCODE -ne 0) { throw "Plugin generation failed" }
python "$repo\tools\verify_plugin.py"
if ($LASTEXITCODE -ne 0) { throw "Plugin verification failed" }

# .mcpb = zip of bridge (manifest.json + server/), forward-slash entries
python "$repo\tools\make_mcpb.py"
Get-ChildItem $dist -File | Select Name, Length
