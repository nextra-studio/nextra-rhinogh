"""dist/nextra-rhinogh-<ver>.mcpb for Claude Desktop: zip of bridge/manifest.json + bridge/server/.
Zip entries use forward slashes (Windows PowerShell's Compress-Archive writes backslashes, which other tools misread)."""
import json, os, sys, zipfile

repo = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
src = os.path.join(repo, "bridge")
ver = json.load(open(os.path.join(src, "manifest.json"), encoding="utf-8"))["version"]
out = os.path.join(repo, "dist", f"nextra-rhinogh-{ver}.mcpb")
os.makedirs(os.path.dirname(out), exist_ok=True)
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    z.write(os.path.join(src, "manifest.json"), "manifest.json")
    for root, _, files in os.walk(os.path.join(src, "server")):
        for f in sorted(files):
            full = os.path.join(root, f)
            z.write(full, os.path.relpath(full, src).replace(os.sep, "/"))
with zipfile.ZipFile(out) as z:
    names = z.namelist()
assert "manifest.json" in names and "server/index.js" in names, names
print(f"{out}: {len(names)} files")
sys.exit(0)
