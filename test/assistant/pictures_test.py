"""Pictures in the Nexiy panel, in a live Rhino: capture the Rhino view into the drawing editor, draw (pen, arrow, box,
text, crop, undo), use it; paste a screenshot with a real Ctrl+V; send a picture to the chat model (API mode, LLM) and to a
CLI. usage: python test/assistant/pictures_test.py [api|cli|all]   (cli uses the CLI set in the settings, default claude)"""
import json, os, subprocess, sys, time
sys.path.insert(0, os.path.dirname(__file__))
import shots as S  # noqa: E402

which = sys.argv[1] if len(sys.argv) > 1 else "all"
T = os.environ["TEMP"]
ok = []


def check(name, cond, info=""):
    ok.append(bool(cond))
    print(("PASS " if cond else "FAIL ") + name + (f"  ({info})" if info else ""))


def ps(code):
    return subprocess.run(["powershell", "-NoProfile", "-Command", code], capture_output=True, text=True).stdout.strip()


# a model with something to see: a red box and a blue sphere
S.py("""import Rhino, System
doc = Rhino.RhinoDoc.ActiveDoc
doc.Objects.Clear()
G = Rhino.Geometry
a = Rhino.DocObjects.ObjectAttributes(); a.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject; a.ObjectColor = System.Drawing.Color.Red
doc.Objects.AddBox(G.Box(G.Plane.WorldXY, G.Interval(0, 4000), G.Interval(0, 4000), G.Interval(0, 4000)), a)
b = Rhino.DocObjects.ObjectAttributes(); b.ColorSource = Rhino.DocObjects.ObjectColorSource.ColorFromObject; b.ObjectColor = System.Drawing.Color.Blue
doc.Objects.AddSphere(G.Sphere(G.Point3d(9000, 2000, 2000), 2000), b)
for v in doc.Views: v.ActiveViewport.DisplayMode = Rhino.Display.DisplayModeDescription.FindByName('Shaded')
doc.Views.ActiveView = [v for v in doc.Views if v.ActiveViewport.Name == 'Perspective'][0]
doc.Views.ActiveView.ActiveViewport.ZoomExtents()
doc.Views.Redraw()
print('model')""")
S.config(assistant_mode="api", assistant_chat="openai", assistant_prefs='{\\"mode\\":\\"llm\\",\\"apply\\":\\"auto\\"}')
S.panel()

# 1) capture -> editor
check("no Grasshopper button while Grasshopper is closed", S.js("return String(document.getElementById('capgh').hidden);") == "true")
S.js("document.getElementById('cap').click(); return 'ok';")
for _ in range(20):
    time.sleep(0.5)
    if S.js("return String(!!document.querySelector('.markup canvas'));") == "true":
        break
check("editor opens with the Rhino view", S.js("return String(!!document.querySelector('.markup canvas'));") == "true")
time.sleep(0.6)
# draw with pointer events in screen coordinates of the canvas: a pen stroke, an arrow, a box, a crop, then undo the crop
draw = """
var c=document.querySelector('.markup canvas'), r=c.getBoundingClientRect();
function ev(t,x,y){ c.dispatchEvent(new PointerEvent(t,{clientX:r.left+x*r.width, clientY:r.top+y*r.height, button:0, bubbles:true, pointerId:1})); }
function stroke(pts){ ev('pointerdown',pts[0][0],pts[0][1]); for (var i=1;i<pts.length;i++) ev('pointermove',pts[i][0],pts[i][1]); ev('pointerup',pts[pts.length-1][0],pts[pts.length-1][1]); }
function tool(t){ document.querySelector('[data-tool='+t+']').click(); }
tool('pen'); stroke([[0.2,0.3],[0.3,0.25],[0.4,0.3],[0.35,0.45],[0.2,0.4]]);
tool('arrow'); stroke([[0.8,0.15],[0.6,0.35]]);
document.querySelector('[data-color=\\"#3e7bfa\\"]').click();
tool('box'); stroke([[0.55,0.4],[0.85,0.75]]);
tool('crop'); stroke([[0.05,0.05],[0.95,0.9]]);
return 'drawn';"""
S.js(draw)
time.sleep(0.4)
# text: click, type, Enter
S.js("""var c=document.querySelector('.markup canvas'), r=c.getBoundingClientRect();
document.querySelector('[data-tool=text]').click();
c.dispatchEvent(new PointerEvent('pointerdown',{clientX:r.left+0.15*r.width, clientY:r.top+0.8*r.height, button:0, bubbles:true, pointerId:1}));
return 'ok';""")
time.sleep(0.3)
S.js("var t=document.querySelector('.mk-text'); t.value='this one'; t.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true})); return 'ok';")
S.shot_panel(os.path.join(T, "nexiy-markup.png"))
S.js("document.querySelector('[data-act=done]').click(); return 'ok';")
time.sleep(1.2)
n = S.js("return String(document.querySelectorAll('#tray .pic').length);")
check("marked-up picture waits in the tray", n == "1", n)
size = S.js("var i=document.querySelector('#tray img'); return i.naturalWidth+'x'+i.naturalHeight+' '+Math.round(i.src.length/1024)+'kB '+i.src.slice(5,15);")
check("picture fitted (<= 1600 px, crop applied)", size and int(size.split('x')[0]) <= 1600, size)

# 2) paste a screenshot with a real Ctrl+V (an image on the clipboard)
ps("Add-Type -AssemblyName System.Windows.Forms,System.Drawing; $b=New-Object System.Drawing.Bitmap 320,200; $g=[System.Drawing.Graphics]::FromImage($b); $g.Clear([System.Drawing.Color]::Gold); $g.FillEllipse([System.Drawing.Brushes]::DarkGreen,100,50,120,100); [System.Windows.Forms.Clipboard]::SetDataObject($b, $true)")
time.sleep(0.5)
L, Tp = [int(v) for v in subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(T, "nx-rect.ps1")], capture_output=True, text=True).stdout.strip().split(',')][:2]
dpr, qx, qy = json.loads(S.js("var r=document.getElementById('q').getBoundingClientRect(); return JSON.stringify([devicePixelRatio, r.left+r.width/2, r.top+r.height/2]);"))
subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", os.path.join(T, "nx-keys.ps1"), "-x", str(int(L + qx * dpr)), "-y", str(int(Tp + qy * dpr)), "-keys", "^v"])
for _ in range(10):
    time.sleep(0.5)
    n = S.js("return String(document.querySelectorAll('#tray .pic').length);")
    if n == "2":
        break
check("Ctrl+V of a screenshot adds a picture", n == "2", n)
S.shot_panel(os.path.join(T, "nexiy-tray.png"))
S.js("document.querySelectorAll('#tray .icon.x')[1].click(); return 'ok';")
check("× removes a picture", S.js("return String(document.querySelectorAll('#tray .pic').length);") == "1")

# 3) send it: API mode, LLM
if which in ("api", "all"):
    a = S.ask("What colours are the two objects in the picture, and which one did I mark with the box? Answer in one sentence.", 120)
    print("   answer:", a)
    low = a.lower()
    check("API chat model sees the picture", "red" in low and "blue" in low, "")
    check("sent message shows the picture", S.js("return String(document.querySelectorAll('.msg.you .thumbs img').length);") == "1")
    check("tray empty after sending", S.js("return String(document.getElementById('tray').hidden);") == "true")
    S.shot_panel(os.path.join(T, "nexiy-picture-answer.png"))

# 4) a CLI
if which in ("cli", "all"):
    S.config(assistant_mode="cli", assistant_cli=os.environ.get("PIC_CLI", "claude"), assistant_cli_model="")
    S.panel()
    S.js("document.getElementById('cap').click(); return 'ok';")
    for _ in range(20):
        time.sleep(0.5)
        if S.js("return String(!!document.querySelector('.markup canvas'));") == "true":
            break
    S.js("document.querySelector('[data-act=done]').click(); return 'ok';")
    time.sleep(1)
    a = S.ask("What colours are the two objects in the picture? Answer in one short sentence, do not change the model.", 240)
    print("   answer:", a)
    check("CLI sees the picture", "red" in a.lower() and "blue" in a.lower())
    S.config(assistant_mode="api")

S.panel(False)
print(f"{sum(ok)}/{len(ok)} passed")
