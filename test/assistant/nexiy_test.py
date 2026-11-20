"""Nexiy test on a live Rhino (Windows): liveliness levels, movement, return home, AI-session activity, variety,
click-through, and the Grasshopper canvas runner. Reads Nexiy's state by reflection (C# through the run tool).
usage: python test/assistant/nexiy_test.py"""
import json, os, re, sys, threading, time
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
import nx  # noqa: E402

HEAD = r'''var asm = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Nextra.Host");
var T = asm.GetType("Nextra.Host.StatusDot");
var BF = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public;
var inst = T.GetField("_instance", BF).GetValue(null);
var win = (Eto.Forms.Window)inst;
object F(string n) => T.GetField(n, BF).GetValue(inst);
'''
STATE = r'''var o = Rhino.UI.RhinoEtoApp.MainWindow.Bounds;
var hm = (Eto.Drawing.Point)T.GetMethod("Home", BF).Invoke(inst, null);
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { level = (int)F("_level"), roaming = (bool)F("_roaming"), shown = (int)F("_shown"), size = (int)F("_size"),
  x = win.Location.X, y = win.Location.Y, w = win.Size.Width, h = win.Size.Height, ox = o.X, oy = o.Y, ow = o.Width, oh = o.Height, hx = hm.X, hy = hm.Y,
  act = F("_act").ToString(), anim = ((Eto.Forms.UITimer)F("_anim")).Started, ct = (bool)F("_clickThrough"), active = (bool)T.GetProperty("Active", BF).GetValue(null) }));'''
fails = []


def cs(code):
    res = nx.call("run", {"lang": "cs", "code": HEAD + code, "label": "nexiy test", "quiet": True}, log=False)[0]
    if not res.get("ok"):
        raise RuntimeError(res.get("text", "")[:400])
    text = res.get("text", "")
    if "stdout:\n" not in text:
        return ""
    body = text.split("stdout:\n", 1)[1]
    return "\n".join(l for l in body.splitlines() if not l.startswith(("rhino:", "gh:", "rhino ", "gh "))).strip()


def state():
    return json.loads(cs(STATE).strip().splitlines()[-1])


def check(name, cond, detail=""):
    print(("PASS " if cond else "FAIL ") + name + (f"  ({detail})" if detail else ""))
    if not cond:
        fails.append(name)


def level(n):
    cs(f'T.GetMethod("SetLevel").Invoke(null, new object[]{{{n}}}); Console.WriteLine("ok");')


def thinking(on):
    cs(f'T.GetProperty("Thinking").SetValue(null, {str(on).lower()}); Console.WriteLine("ok");')


def inside(s):
    return s["x"] >= s["ox"] - 2 and s["y"] >= s["oy"] - 2 and s["x"] + s["w"] <= s["ox"] + s["ow"] + 2 and s["y"] + s["h"] <= s["oy"] + s["oh"] + 2


def sample(n, gap=0.7):
    out = []
    for _ in range(n):
        time.sleep(gap)
        out.append(state())
    return out


def wait_quiet():
    """the last tool call ended more than 8 s ago (else Nexiy still counts it as an AI session)"""
    time.sleep(9)


# ---- default and Calm --------------------------------------------------------------------------------------------------
cfg = json.load(open(os.path.join(os.environ["APPDATA"], "NextraRhinoGh", "config.json"), encoding="utf-8-sig"))
print("config nexiy_level:", cfg.get("nexiy_level", "(unset -> default)"))
check("default level is Lively (1) when unset", cs('Console.WriteLine(T.GetField("DefaultLevel", BF).GetValue(null));') == "1")
s0 = state()
home = (s0["x"], s0["y"])

level(0); thinking(True)
ss = sample(4)
check("Calm: never moves", all((s["x"], s["y"]) == (s0["hx"], s0["hy"]) or (s["x"], s["y"]) == home for s in ss), str([(s["x"], s["y"]) for s in ss]))
check("Calm: never changes size", all(s["shown"] == s["size"] for s in ss))
thinking(False)

# ---- Lively ------------------------------------------------------------------------------------------------------------
level(1); thinking(True)
ss = sample(4)
check("Lively: stays home", all(not s["roaming"] for s in ss) and len({(s["x"], s["y"]) for s in ss}) == 1)
check("Lively: same size", all(s["shown"] == s["size"] for s in ss))
check("Lively: animates in place (acting out)", any(s["anim"] for s in ss))
thinking(False)
time.sleep(1.5)

# ---- Playful -----------------------------------------------------------------------------------------------------------
level(2); thinking(True)
ss = sample(8, 0.6)
pts = {(s["x"], s["y"]) for s in ss}
check("Playful: roams", all(s["roaming"] for s in ss[1:]))
check("Playful: grows", all(s["shown"] > s["size"] for s in ss[1:]), f"shown {ss[-1]['shown']} size {ss[-1]['size']}")
check("Playful: moves (many positions)", len(pts) >= 6, f"{len(pts)} positions")
check("Playful: stays inside the Rhino window", all(inside(s) for s in ss))
check("Playful: clicks pass through while roaming", all(s["ct"] for s in ss[1:]))
span = max(abs(s["x"] - s0["hx"]) + abs(s["y"] - s0["hy"]) for s in ss)
check("Playful: stays near its corner", span < 700, f"max distance {span}px")
thinking(False)
time.sleep(2.5)
s = state()
check("Playful: back home and back to size", not s["roaming"] and s["shown"] == s["size"] and (s["x"], s["y"]) == (s["hx"], s["hy"]), f"at {s['x']},{s['y']} home {s['hx']},{s['hy']} shown {s['shown']}")
check("Playful: clickable again at home", not s["ct"])

# ---- Chaos -------------------------------------------------------------------------------------------------------------
level(3); thinking(True)
ss = sample(10, 0.7)
xs = [s["x"] for s in ss]; ys = [s["y"] for s in ss]
check("Chaos: grows to 64 px", all(s["shown"] >= 64 for s in ss[1:]))
check("Chaos: crosses the Rhino window", (max(xs) - min(xs)) > ss[0]["ow"] * 0.3 or (max(ys) - min(ys)) > ss[0]["oh"] * 0.3, f"x range {max(xs)-min(xs)} y range {max(ys)-min(ys)} of {ss[0]['ow']}x{ss[0]['oh']}")
check("Chaos: stays inside the Rhino window", all(inside(s) for s in ss))

# variety: path families and routines
out = cs(r'''var kinds = new System.Collections.Generic.HashSet<int>(); var combos = new System.Collections.Generic.HashSet<string>();
var np = T.GetMethod("NextPath", BF); var nm = T.GetMethod("NewMoves", BF);
for (int i = 0; i < 300; i++) {
  np.Invoke(inst, new object[]{ System.DateTime.Now });
  var p = F("_path"); kinds.Add((int)p.GetType().GetField("Kind").GetValue(p));
  var m = nm.Invoke(inst, new object[]{ 3, i % 2 == 0 });
  var mt = m.GetType();
  combos.Add($"{mt.GetField("Prop").GetValue(m)}|{mt.GetField("Arms").GetValue(m)}|{mt.GetField("Legs").GetValue(m)}|{mt.GetField("Body").GetValue(m)}|{mt.GetField("Face").GetValue(m)}");
}
Console.WriteLine($"{kinds.Count} {combos.Count}");''')
k, c = map(int, out.split()[-2:])
check("Variety: route families used", k >= 12, f"{k} of 13 families in 300 picks")
check("Variety: different routines", c >= 100, f"{c} distinct routines in 300 picks")
thinking(False)
time.sleep(2.5)

# ---- a real AI session: tool calls with gaps (no thinking flag) --------------------------------------------------------
level(2)
wait_quiet()
def session():
    for _ in range(4):
        nx.call("query", {"scope": "rhino", "level": "summary"}, log=False)
        time.sleep(1.5)
th = threading.Thread(target=session); th.start()
time.sleep(2.0)
ss = sample(4, 0.9)
th.join()
check("AI session: counts as working between calls", all(s["active"] for s in ss))
check("AI session: Playful roams during the session", any(s["roaming"] for s in ss))
time.sleep(9.5)
s = state()
check("AI session: quiet again afterwards", not s["active"] and not s["roaming"] and s["shown"] == s["size"])
acts = []
# the end of the burst: a party (Lively and up) right after it goes quiet
for _ in range(3):
    acts.append(state()["act"]); time.sleep(0.3)
print("acts after the session:", acts)

# ---- Grasshopper: the Chaos runner ---------------------------------------------------------------------------------------
nx.call("gh_edit", {"new": True, "ops": 'h = Number Slider {v=5, r=0..20} "Height"\nc = Circle (P=XY, R=h)'}, log=False)
SHOW_GH = """import Grasshopper, System
ed = Grasshopper.Instances.DocumentEditor
ed.Show()
ed.WindowState = System.Windows.Forms.FormWindowState.Normal
ed.Size = System.Drawing.Size(1300, 850)
print('editor')"""
nx.call("run", {"code": SHOW_GH, "quiet": True}, log=False)
GH = r'''var gt = asm.GetType("Nextra.Host.GhDot");
var GB = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
var rr = (System.Drawing.Rectangle)gt.GetField("_runRect", GB).GetValue(null);
var cv = gt.GetField("_canvas", GB).GetValue(null) as System.Windows.Forms.Control;
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { attached = cv != null, run = gt.GetField("_run", GB).GetValue(null) != null, x = rr.X, y = rr.Y, w = rr.Width, cw = cv?.ClientSize.Width ?? 0, ch = cv?.ClientSize.Height ?? 0 }));'''
time.sleep(2)
g0 = json.loads(cs(GH).splitlines()[-1])
check("GH: Nexiy dot attached to the canvas", g0["attached"], json.dumps(g0))
level(3); thinking(True)
time.sleep(1.5)
gs = []
for _ in range(5):
    time.sleep(0.8)
    gs.append(json.loads(cs(GH).splitlines()[-1]))
check("GH: Chaos runner runs while working", all(g["run"] for g in gs), json.dumps(gs[-1]))
gp = {(g["x"], g["y"]) for g in gs}
check("GH: runner moves around the canvas", len(gp) >= 4, f"{len(gp)} positions")
xs_ = [g["x"] for g in gs]; ys_ = [g["y"] for g in gs]
check("GH: runner goes inside the canvas, not only along the edge", any(60 < g["x"] < g["cw"] - 120 and 60 < g["y"] < g["ch"] - 120 for g in gs), str([(g["x"], g["y"]) for g in gs]))
check("GH: runner stays on the canvas", all(0 <= g["x"] <= g["cw"] and 0 <= g["y"] <= g["ch"] for g in gs))
thinking(False)
time.sleep(1.5)
g1 = json.loads(cs(GH).splitlines()[-1])
check("GH: runner stops when work stops", not g1["run"])

# back to the default
level(1)
print(f"\n{'ALL PASSED' if not fails else 'FAILED: ' + ', '.join(fails)}")
sys.exit(1 if fails else 0)
