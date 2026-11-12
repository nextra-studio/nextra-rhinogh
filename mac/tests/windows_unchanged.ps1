# Proves the Mac changes do not alter the Windows plug-in: builds the Windows host + engine from `main` and from the
# current checkout (same folder, so paths match), without debug info (line numbers move, IL must not), and compares bytes.
# Run from the repo root with a clean working tree and Rhino closed:  powershell -File mac/tests/windows_unchanged.ps1
param([string]$Base = "main")
$ErrorActionPreference = "Stop"
$changed = git diff --name-only $Base HEAD -- src
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("nx-win-" + [Guid]::NewGuid().ToString("N").Substring(0, 8))
function Build($label) {
    foreach ($p in "Nextra.Host", "Nextra.Engine") {
        dotnet build "src/$p/$p.csproj" -c Release -nologo -v q --no-incremental -p:DebugType=none -p:Deterministic=true -o "$tmp/$label" | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$p build failed ($label)" }
    }
}
try {
    Build "branch"
    git checkout $Base -- $changed
    try { Build "base" } finally { git checkout HEAD -- $changed }
    $bad = 0
    foreach ($f in "Nextra.Host.rhp", "Nextra.Engine.dll") {
        $a = (Get-FileHash "$tmp/branch/$f").Hash; $b = (Get-FileHash "$tmp/base/$f").Hash
        "{0,-18} {1}  {2}" -f $f, $(if ($a -eq $b) { "identical" } else { "DIFFERENT" }), $a.Substring(0, 16)
        if ($a -ne $b) { $bad++ }
    }
    "shared files changed vs ${Base}: " + ($changed -join ", ")
    if ($bad) { "FAIL: the Windows build changed"; exit 1 } else { "PASS: Windows host and engine are byte-identical to $Base" }
} finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }
