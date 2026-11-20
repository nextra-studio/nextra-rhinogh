"""Screenshots of the real Nexiy panel for the website: drives the panel in a live Rhino (connection mode, requests,
settings) and captures its window (PrintWindow) and Rhino's viewport / the Grasshopper canvas (view tool).
Used as a library by test/assistant/make_journal_shots.py."""
import json, os, re, subprocess, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
import nx  # noqa: E402

SCRATCH = os.environ.get("SHOTS_TMP", os.path.join(os.environ["TEMP"], "nexiy-shots"))
os.makedirs(SCRATCH, exist_ok=True)
H = '''var asm = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Nextra.Host");
var BF = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var Cfg = asm.GetType("Nextra.Host.Config"); var P = asm.GetType("Nextra.Host.AssistantPanel");
void Set(string k, string v) => Cfg.GetMethod("Set", BF).Invoke(null, new object[]{ k, System.Text.Json.Nodes.JsonValue.Create(v) });
'''
WEB = 'var inst = P.GetField("_instance", BF).GetValue(null); var web = (Eto.Forms.WebView)P.GetField("_web", BF).GetValue(inst);\n'


def cs(code):
    t = nx.call("run", {"lang": "cs", "code": H + code, "quiet": True}, log=False)[0].get("text", "")
    if "stdout:\n" not in t:
        raise RuntimeError(t[:600])
    return "\n".join(l for l in t.split("stdout:\n", 1)[1].splitlines() if not l.startswith(("rhino", "gh"))).strip()


def py(code):
    return nx.call("run", {"code": code, "quiet": True}, log=False)[0].get("text", "")


def config(**kv):
    cs("".join(f'Set("{k}", "{v}"); ' for k, v in kv.items()) + 'Console.WriteLine("ok");')


def panel(open_=True):
    is_open = cs('Console.WriteLine(P.GetProperty("IsOpen", BF).GetValue(null));') == "True"
    if is_open:
        cs('P.GetMethod("Toggle", BF).Invoke(null, null); Console.WriteLine("ok");')
        time.sleep(0.6)
    if open_:
        cs('P.GetMethod("Toggle", BF).Invoke(null, null); Console.WriteLine("ok");')
        time.sleep(3.5)


def js(code):
    """run JavaScript in the panel page; `code` is a function body that returns a string"""
    esc = code.replace("\\", "\\\\").replace('"', '\\"').replace("\n", " ")
    return cs(WEB + f'Console.WriteLine(web.ExecuteScript("{esc}"));')


def ask(text, wait_s=120):
    js(f"var q=document.getElementById('q'); q.value={json.dumps(text)}; document.getElementById('ask').requestSubmit(); return 'sent';")
    t0 = time.time()
    while time.time() - t0 < wait_s:
        time.sleep(2)
        if js("return document.querySelector('.msg.wait') ? 'wait' : 'done';") == "done":
            time.sleep(1.2)
            return js("var m=[...document.querySelectorAll('.msg')]; return m.slice(-1)[0].innerText.replace(/\\n/g,' / ').slice(0,300);")
    return "timeout"


def mask_keys():
    """the saved-key hints show the last 4 characters of real keys: blank them for pictures"""
    js("document.querySelectorAll('.keyrow input').forEach(function(i){ if(i.placeholder.indexOf('saved')===0) i.placeholder='saved …a1b2'; }); return 'ok';")


WINSHOT = os.path.join(os.environ["TEMP"], "nexiy-winshot.ps1")
open(WINSHOT, "w", encoding="utf-8").write(r'''param([string]$title, [string]$out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public static class WS {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static IntPtr Find(string t) { IntPtr f = IntPtr.Zero; EnumWindows((h, l) => { var sb = new StringBuilder(256); GetWindowText(h, sb, 256); if (IsWindowVisible(h) && sb.ToString() == t) { f = h; return false; } return true; }, IntPtr.Zero); return f; }
}
"@
[WS]::SetProcessDPIAware() | Out-Null
$h = [WS]::Find($title); if ($h -eq [IntPtr]::Zero) { "window not found"; exit 1 }
$r = New-Object WS+RECT; [WS]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $dc = $g.GetHdc(); [WS]::PrintWindow($h, $dc, 2) | Out-Null; $g.ReleaseHdc($dc)
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
''')


def shot_panel(path):
    subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", WINSHOT, "-title", "Nexiy", "-out", path], check=True, capture_output=True)
    return path


def view(path, target="rhino", **extra):
    """a capture of the Rhino viewport (target rhino) or the Grasshopper canvas (target gh), copied to path"""
    args = {"target": target, **extra}
    res = nx.call("view", args, log=False)[0]
    imgs = res.get("images", [])
    if not imgs:
        raise RuntimeError(res.get("text", "")[:300])
    import shutil
    shutil.copy(imgs[0], path)
    return path
