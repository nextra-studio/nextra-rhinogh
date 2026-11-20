# Development only: stores the AI keys of a KEY=value file (OPENAI_API_KEY, GEMINI_API_KEY, JEV_API_KEY) in the plug-in's
# key store, exactly as the NextraDecisionApi dialog does (DPAPI, current Windows user, secrets.json), and switches the
# assistant on. Never prints a key. Users use the dialog instead.
param([string]$File = "$env:USERPROFILE\.config\nextra-dev\keys.env", [string]$Decider = "openai", [string]$Chat = "openai")
Add-Type -AssemblyName System.Security
$app = Join-Path $env:APPDATA "NextraRhinoGh"
$entropy = [Text.Encoding]::UTF8.GetBytes("NextraRhinoGh.ai-keys.v1")
$map = @{ OPENAI_API_KEY = "openai"; GEMINI_API_KEY = "gemini"; JEV_API_KEY = "jev" }
$secretsPath = Join-Path $app "secrets.json"
$secrets = @{}
if (Test-Path $secretsPath) { (Get-Content $secretsPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $secrets[$_.Name] = $_.Value } }
foreach ($line in Get-Content $File) {
    if ($line -match '^([A-Z_]+)=(.*)$' -and $map.ContainsKey($Matches[1])) {
        $v = $Matches[2].Trim().Trim('"', "'")
        $enc = [Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($v), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
        $secrets["ai-key-" + $map[$Matches[1]]] = [Convert]::ToBase64String($enc)
        Write-Output ("stored " + $map[$Matches[1]] + " key (..." + $v.Substring([Math]::Max(0, $v.Length - 4)) + ")")
    }
}
$secrets | ConvertTo-Json | Set-Content -Encoding utf8 $secretsPath
$cfgPath = Join-Path $app "config.json"
$conf = @{}
if (Test-Path $cfgPath) { (Get-Content $cfgPath -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $conf[$_.Name] = $_.Value } }
$conf["assistant_enabled"] = $true; $conf["assistant_decider"] = $Decider; $conf["assistant_chat"] = $Chat
$conf | ConvertTo-Json | Set-Content -Encoding utf8 $cfgPath
Write-Output "assistant on: decider $Decider, chat $Chat"
