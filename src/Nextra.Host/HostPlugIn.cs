using System;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;

[assembly: System.Runtime.InteropServices.Guid("6b1f5f0e-8f0a-4c51-9d1e-3a7c2b9e4a11")]
[assembly: PlugInDescription(DescriptionType.Organization, "Nextra")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/Tatsuki0123/nextra-rhinogh")]

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
            if (Config.GetBool("enabled", true)) RhinoApp.Idle += StartOnce;
            else RhinoApp.WriteLine("Nextra: off (run NextraOn to start)");
            return LoadReturnCode.Success;
        }

        void StartOnce(object sender, EventArgs e)
        {
            RhinoApp.Idle -= StartOnce;
            TurnOn();
        }

        public static void TurnOn()
        {
            try { Server.Start(); }
            catch (Exception e) { RhinoApp.WriteLine("Nextra: server failed to start: " + e.Message); }
            try { StatusDot.Show(RhinoApp.MainWindowHandle()); } catch (Exception ex) { RhinoApp.WriteLine("Nextra: status dot failed: " + ex.Message); }
            GhDotHook.Start();
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
        public override string EnglishName => "Nextra";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var go = new Rhino.Input.Custom.GetOption();
            go.SetCommandPrompt($"Nextra {(Server.IsListening ? "on 127.0.0.1:" + Server.Port : "off")} (engine: {EngineLoader.Describe()})");
            int iOn = go.AddOption("On");
            int iOff = go.AddOption("Off");
            int iReload = go.AddOption("ReloadEngine");
            int iRestart = go.AddOption("RestartServer");
            go.AcceptNothing(true);
            if (go.Get() != Rhino.Input.GetResult.Option) return Result.Success;
            int i = go.Option().Index;
            if (i == iOn) NextraOnCommand.Do();
            else if (i == iOff) NextraOffCommand.Do();
            else if (i == iReload) { EngineLoader.ForceReload(); RhinoApp.WriteLine("Nextra: engine reloaded: " + EngineLoader.Describe()); }
            else if (i == iRestart) { Server.Stop(); Server.Start(); }
            return Result.Success;
        }
    }

    public class NextraOnCommand : Command
    {
        public override string EnglishName => "NextraOn";
        public static void Do()
        {
            Config.Set("enabled", true);
            NextraHostPlugIn.TurnOn();
            RhinoApp.WriteLine("Nextra: on");
        }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Do(); return Result.Success; }
    }

    public class NextraOffCommand : Command
    {
        public override string EnglishName => "NextraOff";
        public static void Do()
        {
            Config.Set("enabled", false);
            NextraHostPlugIn.TurnOff();
            RhinoApp.WriteLine("Nextra: off (server stopped, dots removed; NextraOn to start again)");
        }
        protected override Result RunCommand(RhinoDoc doc, RunMode mode) { Do(); return Result.Success; }
    }
}
