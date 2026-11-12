#if NEXTRA_MAC
using System;
using System.Diagnostics;
using Rhino;

namespace Nextra.Host
{
    /// Mac stand-ins for the Windows-only host parts (StatusDot.cs, GhDot.cs, DialogProbe.cs are not compiled on Mac).
    /// Same type and member names, so the shared host code compiles unchanged.

    /// Windows shows a floating WinForms light over the Rhino window. Mac: the state goes to the command line instead
    /// (also on demand: run NextraRhinoGh).
    static class StatusDot
    {
        public static void Show(IntPtr owner) =>
            RhinoApp.WriteLine(Server.IsListening
                ? $"NextraRhinoGh: ready on 127.0.0.1:{Server.Port} (Mac: no status dot; run NextraRhinoGh for the state)"
                : "NextraRhinoGh: server down; run NextraRhinoGh > RestartServer");

        public static void CloseDot() { }
    }

    /// The Grasshopper canvas dot is drawn through WinForms events; left out on Mac for now.
    static class GhDotHook
    {
        public static void Start() { }
        public static void Stop() { }
    }

    /// Windows reads blocking dialogs through user32 / UI Automation. No equivalent on Mac yet: report nothing, so a
    /// busy Rhino shows up as a plain timeout instead of a dialog description.
    static class DialogProbe
    {
        public static IntPtr MainHandle;
        public static string Describe() => null;
        public static string AutoDismiss() => null;
        public static string Dismiss() => "dialog detection is not available on Mac — ask the user to answer the dialog in Rhino";
    }

    /// Clipboard and Finder through the standard macOS command-line tools (no UI framework needed).
    static class MacShell
    {
        public static void CopyText(string text)
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true, UseShellExecute = false });
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            p.WaitForExit(3000);
        }

        public static void Reveal(string dir)
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(dir);
            Process.Start(psi)?.Dispose();
        }
    }
}
#endif
