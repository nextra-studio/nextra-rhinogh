"""Build host + engine; print compact, de-duplicated errors/warnings."""
import subprocess, sys, re, os
root = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "src")
projs = sys.argv[1:] or ["Nextra.Engine"]
ok = True
for p in projs:
    r = subprocess.run(["dotnet", "build", os.path.join(root, p, p + ".csproj"), "-c", "Debug", "-v", "q", "-nologo"], capture_output=True, text=True)
    seen = set()
    for line in (r.stdout + r.stderr).splitlines():
        if " error " in line or (": warning CS" in line and "CS8" not in line):
            m = re.sub(r"\s*\[[^\]]*\.csproj\]$", "", line)
            m = re.sub(r"^.*?src\\", "", m)
            if m not in seen:
                seen.add(m); print(m)
    print(f"{p}: {'OK' if r.returncode == 0 else 'FAILED'}")
    ok &= r.returncode == 0
sys.exit(0 if ok else 1)
