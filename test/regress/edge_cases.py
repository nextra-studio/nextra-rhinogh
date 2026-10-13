"""Live audit of tool edge cases, with repeatable before/after images.

Run on an empty Rhino document and empty active GH canvas:
    python test/regress/edge_cases.py before
    python test/regress/edge_cases.py after
Replies, images and assertions go to out/audit_<label>. User memory is not changed.
"""
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
import nx

LABEL = sys.argv[1] if len(sys.argv) > 1 else "after"
OUT = Path(__file__).parent / "out" / ("audit_" + LABEL)
OUT.mkdir(parents=True, exist_ok=True)
checks = []
replies = []


def call(tool, args, name=None):
    result, *_ = nx.call(tool, args, log=False)
    name = name or f"{len(replies):02d}_{tool}"
    replies.append({"name": name, "tool": tool, "args": args, "result": result})
    (OUT / (name + ".json")).write_text(json.dumps(replies[-1], indent=2), encoding="utf-8")
    for i, image in enumerate(result.get("images", [])):
        shutil.copy2(image, OUT / f"{name}_{i}.png")
    return result


def check(ok, title, detail=""):
    checks.append({"ok": bool(ok), "title": title, "detail": detail})
    print(("PASS " if ok else "FAIL ") + title + (": " + detail if detail else ""), flush=True)


def ck(result):
    match = re.search(r"checkpoint (c\d+)", result["text"])
    return match[1] if match else None


def run(code, name=None):
    return call("run", {"code": code}, name)


def main():
    # Pin every HTTP call to the same process for the duration of this audit.
    info = nx.server()
    os.environ["NEXTRA_PID"] = str(info["pid"])
    rh = call("query", {"scope": "rhino"})["text"]
    gh = call("query", {"scope": "gh"})["text"]
    if "0 objects" not in rh or not ("0 components, 0 params, 0 groups" in gh or "no Grasshopper document" in gh):
        raise RuntimeError("Audit requires empty documents; refusing to clear existing work")
    run("""v = doc.Views.ActiveView.ActiveViewport
sc.sticky['audit.camera'] = (Rhino.DocObjects.ViewportInfo(v), v.Name, v.CameraTarget, v.GetConstructionPlane(), v.DisplayMode)
sc.sticky['audit.modified'] = doc.Modified
sc.sticky['audit.ghdoc'] = ghdoc()
print('saved camera')""")
    try:
        call("memory", {"action": "list"})
        for filt in ["shorthand", "workflows", "api/gh-python", "plugins/Karamba3D"]:
            r = call("query", {"scope": "docs", "filter": filt})
            check(r.get("ok") and len(r["text"]) > 100, "docs " + filt)
        r = call("query", {"scope": "docs", "filter": "audit_missing_document_xyz"})
        check("no doc" in r["text"], "missing doc explained")
        r = call("query", {"scope": "components", "filter": "Loft, Divide Curve"})
        check("Loft @" in r["text"] and "Divide Curve @" in r["text"], "installed component exact search")
        r = call("query", {"scope": "components", "filter": "@Karamba3D"})
        check("in @Karamba3D" in r["text"], "installed plugin category")
        r = call("query", {"scope": "components", "filter": "divde curve"})
        check("Divide Curve @" in r["text"], "component typo search", r["text"][:180])
        r = call("query", {"scope": "components", "filter": "zzzxxyy_no_component"})
        check("nothing found" in r["text"], "unrelated search returns no matches", r["text"][:180])

        r = call("gh_edit", {"new": True, "ops": "n = Number Slider {v=5000, r=1..10000}\nc = Circle (R=n)"})
        check(r.get("ok"), "build GH definition")
        handles = dict(re.findall(r"\b(n|c)=(g\d+)", r["text"]))
        circle = handles["c"]
        r = call("view", {"target": "gh_preview", "view": "top", "focus": circle}, "preview_handle")
        check(r.get("ok") and "zoom to GH preview" in r["text"] and "1 component(s)" in r["text"], "GH handle is not a grid cell", r["text"])
        r = call("view", {"target": "gh", "focus": circle}, "canvas_handle")
        check(r.get("ok") and "focus 1 object(s)" in r["text"], "canvas handle is not a grid cell", r["text"])
        r = call("gh_edit", {"atomic": True, "ops": f"{circle} (P=XY)"})
        check(r.get("ok"), "world-plane XY literal", r["text"][:200])
        r = call("gh_edit", {"ops": f"{handles['n']} {{v=6000}}"})
        call("undo", {"checkpoint": ck(r)})
        r = call("query", {"scope": "gh", "filter": handles["n"]})
        check("v=5000" in r["text"], "GH slider undo")
        r = call("gh_edit", {"atomic": True, "ops": "a = Number Slider {v=2}\nb = NoSuchAuditComponent"})
        check(not r.get("ok") and "rolled back" in r["text"], "atomic failure rollback")

        # Keep GH previews out of the Rhino framing images.
        call("gh_edit", {"ops": f"{circle} {{hide}}"})
        r = run("doc.Objects.AddLine(rg.Point3d(0,0,0), rg.Point3d(10000,0,0))")
        line = re.search(r"\+ (r\d+) line", r["text"])[1]
        run("""v = doc.Views.ActiveView.ActiveViewport
sc.sticky['audit.before'] = (v.Name, v.CameraLocation, v.CameraDirection, v.CameraUp, v.CameraTarget, v.GetConstructionPlane().Plane, v.IsPerspectiveProjection)
print('camera recorded')""")
        call("view", {"view": "right", "focus": line})
        r = run("""v = doc.Views.ActiveView.ActiveViewport
now = (v.Name, v.CameraLocation, v.CameraDirection, v.CameraUp, v.CameraTarget, v.GetConstructionPlane().Plane, v.IsPerspectiveProjection)
print('camera_equal', now == sc.sticky['audit.before'])
print('before', sc.sticky['audit.before'])
print('after', now)""", "camera_restore")
        check("camera_equal True" in r["text"], "viewport name/camera/target/CPlane restored", r["text"][:600])
        call("view", {"view": "top", "focus": line, "isolate": True}, "line_overview")
        r = call("view", {"focus": "A4:C5", "tags": "on"}, "line_crop")
        check("1 object(s) in frame" in r["text"] and line in r["text"], "visible crossing line counted and tagged", r["text"])
        r = call("query", {"filter": "cells:A4:H5"})
        check(line in r["text"], "cell query includes crossing line", r["text"])
        r = call("view", {"view": "top", "focus": line, "tags": "off"}, "tags_off")
        check(r.get("ok") and "Too many" not in r["text"], "tags off has no false crowd warning", r["text"])

        r = run("doc.Layers.Add('AuditLayerOnly', System.Drawing.Color.Red)")
        checkpoint = ck(r)
        if checkpoint:
            call("undo", {"checkpoint": checkpoint})
        r = run("print('layer_absent', doc.Layers.FindName('AuditLayerOnly') is None)")
        check(checkpoint and "layer_absent True" in r["text"], "layer-only edit gets checkpoint and undoes")
        r = run("""idx = doc.Layers.Add('AuditLayerObject', System.Drawing.Color.Red)
a = Rhino.DocObjects.ObjectAttributes(); a.LayerIndex = idx
doc.Objects.AddPoint(rg.Point3d(1,2,3), a)""")
        call("undo", {"checkpoint": ck(r)})
        r = run("print('layer_absent', doc.Layers.FindName('AuditLayerObject') is None)")
        check("layer_absent True" in r["text"], "undo removes added layer with objects")

        r = run("""layer = doc.Layers[doc.Layers.CurrentLayerIndex]
sc.sticky['audit.layer'] = (layer.Id, layer.Name, layer.Color.ToArgb())
layer.Name = 'AuditRenamed'; layer.Color = System.Drawing.Color.Blue; layer.CommitChanges()""")
        call("undo", {"checkpoint": ck(r)})
        r = run("""layer = doc.Layers[doc.Layers.CurrentLayerIndex]
print('layer_equal', (layer.Id, layer.Name, layer.Color.ToArgb()) == sc.sticky.pop('audit.layer'))""")
        check("layer_equal True" in r["text"], "undo restores layer name and colour")
        r = run("""parent = doc.Layers.Add('AuditParent', System.Drawing.Color.Red)
child = Rhino.DocObjects.Layer(); child.Name = 'AuditChild'; child.ParentLayerId = doc.Layers[parent].Id
idx = doc.Layers.Add(child)
doc.Layers.SetCurrentLayerIndex(idx, True)
doc.Objects.AddPoint(rg.Point3d(1,2,3))""")
        call("undo", {"checkpoint": ck(r)})
        r = run("print('nested_absent', doc.Layers.FindName('AuditParent') is None, doc.Layers.FindName('AuditChild') is None, doc.Layers[doc.Layers.CurrentLayerIndex].Name)")
        check("nested_absent True True Default" in r["text"], "nested layer/current-layer undo")
        r = call("gh_edit", {"bake": [circle], "layer": "AuditBake"})
        call("undo", {"checkpoint": ck(r)})
        r = run("print('bake_layer_absent', doc.Layers.FindName('AuditBake') is None)")
        check("bake_layer_absent True" in r["text"], "bake undo removes created layer")

        # Invoke the actual stdio bridge, not nx's HTTP shortcut.
        msg = {"jsonrpc": "2.0", "id": 1, "method": "tools/call", "params": {"name": "run", "arguments": {"code": "print('ask bypass')"}}}
        p = subprocess.run(["node", str(ROOT / "bridge/server/index.js")], input=json.dumps(msg)+"\n", capture_output=True, text=True, encoding="utf-8", env={**os.environ, "NEXTRA_MODE": "ask"}, timeout=30)
        result = json.loads(p.stdout)["result"]
        check(result.get("isError"), "ask mode rejects direct run", str(result)[:250])
    finally:
        cleanup = run("""for o in list(doc.Objects): doc.Objects.Delete(o, True)
for i in range(doc.Layers.Count-1,-1,-1):
    if not doc.Layers[i].IsDeleted and doc.Layers[i].Name.startswith('Audit'):
        doc.Layers.Purge(i, True)
d = ghdoc()
for o in list(d.Objects): d.RemoveObject(o, False)
d.NewSolution(False)
Grasshopper.Instances.ActiveCanvas.Document = sc.sticky.pop('audit.ghdoc')
Grasshopper.Instances.DocumentServer.RemoveDocument(d)
v = doc.Views.ActiveView.ActiveViewport
saved, name, target, cp, mode = sc.sticky.pop('audit.camera')
v.SetViewProjection(saved, True); v.Name = name; v.SetCameraTarget(target, False)
v.SetConstructionPlane(cp); v.DisplayMode = mode
doc.Modified = sc.sticky.pop('audit.modified')
doc.Views.Redraw()
print('cleanup complete')""")
        check(cleanup.get("ok") and "cleanup complete" in cleanup["text"], "audit cleanup restores the empty session")
        (OUT / "checks.json").write_text(json.dumps(checks, indent=2), encoding="utf-8")
        print(f"{sum(c['ok'] for c in checks)}/{len(checks)} passed; evidence: {OUT}")
    return all(c["ok"] for c in checks)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.exit(0 if main() else 1)
