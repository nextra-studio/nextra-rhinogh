using System;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

[assembly: System.Runtime.InteropServices.Guid("6b1f5f0e-8f0a-4c51-9d1e-3a7c2b9e4a11")]
[assembly: PlugInDescription(DescriptionType.Organization, "NextraRhinoGh")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/nextra-studio/nextra-rhinogh")]

namespace Nextra.Host
{
    /// Thin host: localhost server + UI-thread dispatcher + hot-reloadable engine + status dots.
    /// Everything Rhino/GH specific lives in Nextra.Engine.dll so it can be rebuilt without restarting Rhino.
    public class NextraHostPlugIn : PlugIn
    {
        public static NextraHostPlugIn Instance { get; private set; }
        public NextraHostPlugIn() { Instance = this; }

        public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // never start inside headless hosts (Rhino.Compute / Hops compute.geometry.exe also load plug-ins)
            var proc = System.Diagnostics.Process.GetCurrentProcess().ProcessName;
            if (RhinoApp.IsRunningHeadless || !proc.StartsWith("Rhino", StringComparison.OrdinalIgnoreCase))
                return LoadReturnCode.Success;
            try { AppDataMigration.Ensure(); }
            catch (Exception e) { RhinoApp.WriteLine("NextraRhinoGh: old settings were not copied: " + e.Message); }
            if (Config.GetBool("enabled", true)) RhinoApp.Idle += StartOnce;
            else RhinoApp.WriteLine("NextraRhinoGh: off (run NextraRhinoGh > On to start)");
            return LoadReturnCode.Success;
        }

        void StartOnce(object sender, EventArgs e)
        {
            RhinoApp.Idle -= StartOnce;
            TurnOn();
        }

        public static void TurnOn()
        {
            try { DialogProbe.MainHandle = RhinoApp.MainWindowHandle(); } catch { }   // cached: the probe runs off the UI thread
            try { Server.Start(); }
            catch (Exception e) { RhinoApp.WriteLine("NextraRhinoGh: server failed to start: " + e.Message); }
            try { StatusDot.Show(RhinoApp.MainWindowHandle()); } catch (Exception ex) { RhinoApp.WriteLine("NextraRhinoGh: status dot failed: " + ex.Message); }
            GhDotHook.Start();
            var userFolder = InitUserFolder();
            if (userFolder != null) RhinoApp.WriteLine("NextraRhinoGh: user folder " + userFolder + " (preferences and workflows persist across updates)");
            InstallBridge();
        }

        /// Stable path of the MCP bridge for clients configured by hand (ChatGPT desktop, Codex, others):
        /// the Yak package folder has the version in its name, this path does not.
        public static string BridgePath => System.IO.Path.Combine(Server.AppDir, "bridge", "index.js");

        /// Copies the bridge shipped next to the .rhp (Yak package: bridge/server/index.js) to BridgePath.
        static void InstallBridge()
        {
            try
            {
                var src = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(NextraHostPlugIn).Assembly.Location) ?? "", "bridge", "server", "index.js");
                if (!System.IO.File.Exists(src)) return;   // dev install: no bridge next to the .rhp
                if (System.IO.File.Exists(BridgePath) && System.IO.File.ReadAllText(src) == System.IO.File.ReadAllText(BridgePath)) return;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(BridgePath));
                System.IO.File.Copy(src, BridgePath, true);
            }
            catch (Exception e) { RhinoApp.WriteLine("NextraRhinoGh: bridge not copied: " + e.Message); }
        }

        /// Creates the user folder with its default files right after install (first start), via the engine.
        /// Returns its path, or null if the engine could not be reached.
        public static string InitUserFolder()
        {
            try
            {
                EngineLoader.EnsureFresh();
                var res = System.Text.Json.Nodes.JsonNode.Parse(EngineLoader.Call("memory", "{\"action\":\"init\"}"));
                var text = (string)res?["text"];
                return text != null && text.StartsWith("user folder ") ? text.Substring(12).Trim() : null;
            }
            catch (Exception e) { RhinoApp.WriteLine("NextraRhinoGh: user folder not created: " + e.Message); return null; }
        }

        public static void TurnOff()
        {
            StatusDot.CloseDot();
            GhDotHook.Stop();
            try { Server.Stop(); } catch { }
            try { EngineLoader.Unload(); } catch { }
        }

        protected override void OnShutdown() => TurnOff();
    }

    public class NextraCommand : Command
    {
        public override string EnglishName => "NextraRhinoGh";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new Rhino.Input.Custom.GetOption();
            go.SetCommandPrompt($"NextraRhinoGh {(Server.IsListening ? "on 127.0.0.1:" + Server.Port : "off")} (engine: {EngineLoader.Describe()})");
            int iOn = go.AddOption("On");
            int iOff = go.AddOption("Off");
            int iReload = go.AddOption("ReloadEngine");
            int iRestart = go.AddOption("RestartServer");
            int iBridge = go.AddOption("CopyBridgePath");
            go.AcceptNothing(true);
            if (go.Get() != Rhino.Input.GetResult.Option) return Result.Success;
            int i = go.Option().Index;
            if (i == iBridge)
            {
                var p = NextraHostPlugIn.BridgePath;
                try { System.Windows.Clipboard.SetText(p); } catch { }
                RhinoApp.WriteLine(System.IO.File.Exists(p)
                    ? "NextraRhinoGh: bridge path copied to the clipboard: " + p + "  (MCP server: command node, argument this path)"
                    : "NextraRhinoGh: no bridge at " + p + " (it is copied there when the Yak package starts)");
                return Result.Success;
            }
            if (i == iOn) On();
            else if (i == iOff) Off();
            else if (i == iReload) { EngineLoader.ForceReload(); RhinoApp.WriteLine("NextraRhinoGh: engine reloaded: " + EngineLoader.Describe()); }
            else if (i == iRestart) { Server.Stop(); Server.Start(); }
            return Result.Success;
        }

        /// On/Off are remembered across Rhino restarts.
        static void On()
        {
            Config.Set("enabled", true);
            NextraHostPlugIn.TurnOn();
            RhinoApp.WriteLine("NextraRhinoGh: on");
        }

        static void Off()
        {
            Config.Set("enabled", false);
            NextraHostPlugIn.TurnOff();
            RhinoApp.WriteLine("NextraRhinoGh: off (server stopped, dots removed; NextraRhinoGh > On to start again)");
        }
    }

    /// Opens the user's NextraRhinoGh folder (preferences.md, workflows/, plugins/, scripts/) in Explorer;
    /// the engine creates it with default files on first use.
    public class NextraFolderCommand : Command
    {
        public override string EnglishName => "NextraRhinoGhFolder";
        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var dir = NextraHostPlugIn.InitUserFolder() ?? System.IO.Path.Combine(Server.AppDir, "user");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
            RhinoApp.WriteLine("NextraRhinoGh: your folder " + dir + " (preferences.md, workflows, plugins, scripts — see README.md there)");
            return Result.Success;
        }
    }
}
