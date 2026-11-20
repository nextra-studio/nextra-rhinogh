"""The assistant panel end to end in a live Rhino: sets the connection (api or cli), opens the panel, types a request into
it, waits for the answer and reads the chat back. usage: python test/assistant/panel_test.py cli claude "request" [seconds]
                                                     python test/assistant/panel_test.py api openai "request" [seconds]"""
import json, os, sys, time
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "tools"))
import nx  # noqa: E402

mode, who, text = sys.argv[1], sys.argv[2], sys.argv[3]
wait_s = int(sys.argv[4]) if len(sys.argv) > 4 else 90
H = '''var asm = System.AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Nextra.Host");
var BF = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
var Cfg = asm.GetType("Nextra.Host.Config"); var P = asm.GetType("Nextra.Host.AssistantPanel");
void Set(string k, string v) => Cfg.GetMethod("Set", BF).Invoke(null, new object[]{ k, System.Text.Json.Nodes.JsonValue.Create(v) });
'''


def cs(code):
    t = nx.call("run", {"lang": "cs", "code": H + code, "quiet": True}, log=False)[0].get("text", "")
    if "stdout:\n" not in t:
        raise RuntimeError(t[:500])
    return "\n".join(l for l in t.split("stdout:\n", 1)[1].splitlines() if not l.startswith(("rhino", "gh"))).strip()


setup = f'Set("assistant_mode", "{mode}"); ' + (f'Set("assistant_cli", "{who}"); Set("assistant_cli_model", ""); ' if mode == "cli" else f'Set("assistant_chat", "{who}"); ')
setup += 'Set("assistant_prefs", "{\\"mode\\":\\"' + os.environ.get("PANEL_MODE", "llm") + '\\",\\"apply\\":\\"auto\\"}"); '
setup += 'if (P.GetProperty("IsOpen", BF).GetValue(null) is true) P.GetMethod("Toggle", BF).Invoke(null, null); P.GetMethod("Toggle", BF).Invoke(null, null); Console.WriteLine("panel open");'
print(cs(setup))
time.sleep(4)
WEB = 'var inst = P.GetField("_instance", BF).GetValue(null); var web = (Eto.Forms.WebView)P.GetField("_web", BF).GetValue(inst);\n'
js_send = json.dumps(text)
print(cs(WEB + 'Console.WriteLine(web.ExecuteScript("var q=document.getElementById(\'q\'); if(!q) return \'not ready\'; q.value=' + js_send.replace('"', '\\"') + '; document.getElementById(\'ask\').requestSubmit(); return \'sent\';"));'))
t0 = time.time()
last = ""
while time.time() - t0 < wait_s:
    time.sleep(3)
    out = cs(WEB + 'Console.WriteLine(web.ExecuteScript("var w=document.querySelector(\'.msg.wait\'); var m=[...document.querySelectorAll(\'.msg\')]; return (w?\'WAIT:\'+w.innerText:\'DONE\') + \' || \' + m.slice(-1)[0].innerText.replace(/\\\\n/g,\' / \').slice(0,600);"));')
    if out != last:
        print(f"{time.time() - t0:5.1f}s", out[:400])
        last = out
    if out.startswith("DONE"):
        break
