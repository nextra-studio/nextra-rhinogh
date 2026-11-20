"""The panel's snip in a live Rhino (Windows): the camera hides the panel, the screen freezes, a real mouse drag over the
Rhino view snips it, the editor opens with the Rhino objects in it, a box drawn over the sphere becomes "a box on r…",
and the chat model names the boxed object by its handle.  usage: python test/assistant/snip_test.py"""
import json, os, subprocess, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import shots as S  # noqa: E402

T = os.environ["TEMP"]
ok = []


def check(name, cond, info=""):
    ok.append(bool(cond))
    print(("PASS " if cond else "FAIL ") + name + (f"  ({info})" if info else ""))


DRAG = os.path.join(T, "nx-drag.ps1")
open(DRAG, "w", encoding="utf-8").write(r'''param([int]$x0, [int]$y0, [int]$x1, [int]$y1)
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class D { [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
 [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware(); }
"@
[D]::SetProcessDPIAware() | Out-Null
[D]::SetCursorPos($x0, $y0) | Out-Null; Start-Sleep -Milliseconds 200
[D]::mouse_event(2,0,0,0,[IntPtr]::Zero); Start-Sleep -Milliseconds 100
for ($i = 1; $i -le 12; $i++) { [D]::SetCursorPos([int]($x0 + ($x1-$x0)*$i/12), [int]($y0 + ($y1-$y0)*$i/12)) | Out-Null; Start-Sleep -Milliseconds 25 }
[D]::mouse_event(4,0,0,0,[IntPtr]::Zero)
''')

# the model, Rhino in front and not minimised, one big Perspective view
print(S.py("""import Rhino, System
doc = Rhino.RhinoDoc.ActiveDoc
doc.Objects.Clear()
G = Rhino.Geometry
a = Rhino.DocObjects.ObjectAttributes(); a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject; a.ObjectColor = System.Drawing.Color.Red
doc.Objects.AddBox(G.Box(G.Plane.WorldXY, G.Interval(0, 4000), G.Interval(0, 4000), G.Interval(0, 4000)), a)
b = Rhino.DocObjects.ObjectAttributes(); b.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject; b.ObjectColor = System.Drawing.Color.Blue
doc.Objects.AddSphere(G.Sphere(G.Point3d(9000, 2000, 2000), 2000), b)
v = [v for v in doc.Views if v.ActiveViewport.Name == 'Perspective'][0]
doc.Views.ActiveView = v
if v.Maximized: v.Maximized = False
v.ActiveViewport.DisplayMode = Rhino.Display.DisplayModeDescription.FindByName('Shaded')
v.ActiveViewport.ZoomExtents()
doc.Views.Redraw()
print('model')""")[-60:])
S.cs('''[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(System.IntPtr h, int c);
[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetForegroundWindow(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(System.IntPtr h);
var hw = Rhino.RhinoApp.MainWindowHandle(); if (IsIconic(hw)) ShowWindow(hw, 9); SetForegroundWindow(hw); Console.WriteLine("front");''')
time.sleep(1.5)
handles = S.py("import Rhino\nfrom System import Guid\nfor o in Rhino.RhinoDoc.ActiveDoc.Objects: print(o.ObjectType, o.Id)")
for _ in range(30):
    rect = S.cs('var s = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView.ScreenRectangle; Console.WriteLine(s.X+","+s.Y+","+s.Width+","+s.Height);')
    vx, vy, vw, vh = [int(v) for v in rect.split(",")]
    if vw > 300:
        break
    if _ in (2, 8):   # a maximised view can come back 0 x 0 after a restart: restore and maximise it again
        S.py("import Rhino\nv = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView\nv.Maximized = False\nv.Maximized = True\nRhino.RhinoDoc.ActiveDoc.Views.Redraw()\nprint(1)")
    time.sleep(1)
print("view on screen", vx, vy, vw, vh)
check("Rhino view on screen", vw > 300 and vx > -20000, rect)

S.config(assistant_mode="api", assistant_chat="openai", assistant_prefs='{\\"mode\\":\\"llm\\",\\"apply\\":\\"auto\\"}')
S.panel()
S.js("document.getElementById('cap').click(); return 'ok';")
time.sleep(1.4)
hidden = S.cs('var inst = P.GetField("_instance", BF).GetValue(null); Console.WriteLine(((Eto.Forms.Window)inst).Visible);')
check("panel steps aside while snipping", hidden == "False", hidden)
# the overlay is up: drag over the view (inset)
subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", DRAG, "-x0", str(vx + 30), "-y0", str(vy + 30), "-x1", str(vx + vw - 30), "-y1", str(vy + vh - 30)])
for _ in range(30):
    time.sleep(0.5)
    if S.js("return String(!!document.querySelector('.markup canvas'));") == "true":
        break
title = S.js("var m=document.querySelector('.markup'); return m ? m.querySelector('b').textContent : 'none';")
check("editor opens with the snip and its objects", "2 Rhino objects" in title, title)
S.shot_panel(os.path.join(T, "nexiy-snip-editor.png"))
# a blue box over the right half (the sphere is right of the cube)
S.js("""var c=document.querySelector('.markup canvas'), r=c.getBoundingClientRect();
function ev(t,x,y){ c.dispatchEvent(new PointerEvent(t,{clientX:r.left+x*r.width, clientY:r.top+y*r.height, button:0, bubbles:true, pointerId:1})); }
document.querySelector('[data-tool=box]').click();
document.querySelector('[data-color=\\"#3e7bfa\\"]').click();
ev('pointerdown',0.52,0.08); ev('pointermove',0.8,0.5); ev('pointerup',0.98,0.95); return 'ok';""")
S.js("document.querySelector('[data-act=done]').click(); return 'ok';")
time.sleep(1.2)
tip = S.js("var i=document.querySelector('#tray img'); return i ? i.title : 'none';")
check("picture in the tray knows its objects", "2 Rhino objects" in tip, tip)
a = S.ask("Which object did I mark with the blue box? Reply with only its handle, like r12, and its type.", 120)
print("   answer:", a)
import nx  # noqa: E402
listing = nx.call("query", {"scope": "rhino", "level": "list", "quiet": True}, log=False)[0]["text"]
sphere = next((l.split()[0] for l in listing.splitlines() if "@(9000" in l), "?")
check(f"the model names the boxed object (the sphere, {sphere})", sphere in a.replace(",", " ").split(), a)
S.shot_panel(os.path.join(T, "nexiy-snip-answer.png"))

# Esc cancels a snip and the panel comes back
S.js("document.getElementById('cap').click(); return 'ok';")
time.sleep(1.4)
subprocess.run(["powershell", "-NoProfile", "-Command", "Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{ESC}')"])
time.sleep(1.2)
back = S.cs('var inst = P.GetField("_instance", BF).GetValue(null); Console.WriteLine(((Eto.Forms.Window)inst).Visible);')
check("Esc cancels, the panel comes back", back == "True", back)
check("no editor after a cancelled snip", S.js("return String(!!document.querySelector('.markup'));") == "false")
S.cs('foreach (var w in Eto.Forms.Application.Instance.Windows.ToList()) if (w.Title == "Nexiy snip") w.Close(); Console.WriteLine("ok");')
S.panel(False)
print(f"{sum(ok)}/{len(ok)} passed")
