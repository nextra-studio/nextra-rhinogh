"""Run inside Rhino (via nx run --code): start McNeel Rhino-MCP-Platform on :5123 and RhinoMCP's socket server (:1999)."""
import System, Rhino
out = []
# McNeel: its command is MCPStart (RhinoMCP also has an MCPStart; call McNeel's through its plug-in when both exist)
ok = Rhino.RhinoApp.RunScript("_-MCPStart _Port 5123 _Enter", False)
out.append("mcneel MCPStart " + str(ok))
# RhinoMCP: load the plug-in and start its server through the controller (no command name clash)
pid = None
for k, v in Rhino.PlugIns.PlugIn.GetInstalledPlugIns().items():
    if v.lower() == "rhinomcp":
        pid = k
if pid is None:
    # installed with Yak while Rhino was running: load it from its package folder
    import glob, os
    hits = glob.glob(os.path.join(os.environ["APPDATA"], "McNeel", "Rhinoceros", "packages", "8.0", "rhinomcp", "*", "net8.0", "rhinomcp.rhp"))
    if hits:
        res, pid = Rhino.PlugIns.PlugIn.LoadPlugIn(hits[0])
        out.append("rhinomcp loaded from path: " + str(res))
if pid is None or pid == System.Guid.Empty:
    out.append("rhinomcp plug-in not installed")
else:
    Rhino.PlugIns.PlugIn.LoadPlugIn(pid)
    asm = [a for a in System.AppDomain.CurrentDomain.GetAssemblies() if a.GetName().Name.lower() == "rhinomcp"]
    if not asm:
        out.append("rhinomcp assembly not loaded")
    else:
        t = asm[0].GetType("RhinoMCPPlugin.RhinoMCPServerController")
        if t.GetMethod("IsServerRunning").Invoke(None, None):
            out.append("rhinomcp already running")
        else:
            t.GetMethod("StartServer").Invoke(None, None)
            out.append("rhinomcp started " + str(t.GetMethod("IsServerRunning").Invoke(None, None)))
print("; ".join(out))
