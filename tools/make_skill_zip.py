"""dist/nextra-rhinogh-skill.zip for claude.ai "Upload a skill": the folder nextra-rhinogh/ with SKILL.md at its top.
Zip entries use forward slashes (Windows PowerShell's Compress-Archive writes backslashes, which other tools misread)."""
import os, sys, zipfile

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = os.path.join(repo, "skill", "nextra")
out = os.path.join(repo, "dist", "nextra-rhinogh-skill.zip")
os.makedirs(os.path.dirname(out), exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(src):
        for f in sorted(files):
            full = os.path.join(root, f)
            rel = os.path.relpath(full, src).replace(os.sep, "/")
            z.write(full, "nextra-rhinogh/" + rel)
with zipfile.ZipFile(out) as z:
    names = z.namelist()
assert "nextra-rhinogh/SKILL.md" in names, "SKILL.md missing"
print(f"{out}: {len(names)} files")
sys.exit(0)
