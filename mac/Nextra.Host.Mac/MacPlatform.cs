#if NEXTRA_MAC
using System;
using System.Diagnostics;

namespace Nextra.Host
{
    /// The only Mac stand-ins left. The status dot, the Grasshopper canvas dot and the clipboard are shared code (Eto)
    /// and compile on Mac unchanged; only DialogProbe.cs is excluded from the Mac build.

    /// Windows reads blocking dialogs of Rhino through user32 / UI Automation; no UI library can read another window's
    /// contents, and macOS would need the Accessibility API (with a permission prompt). Until then: report nothing, so a
    /// busy Rhino shows up as a plain timeout instead of a dialog description. Deliberate, documented exception.
    static class DialogProbe
    {
        public static IntPtr MainHandle;
        public static string Describe() => null;
        public static string AutoDismiss() => null;
        public static string Dismiss() => "dialog detection is not available on Mac — ask the user to answer the dialog in Rhino";
    }

    /// Windows undoes an Eto.Wpf blur-behind here (src/Nextra.Host/WindowsQuirks.cs). Eto on Mac has no such quirk.
    static class PlatformQuirks
    {
        public static void AfterShow(Eto.Forms.Window w) { }
    }

    /// Opening a folder: Finder on Mac (explorer.exe on Windows, HostPlugIn.cs). Not a UI-library task.
    static class MacShell
    {
        public static void Reveal(string dir)
        {
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            psi.ArgumentList.Add(dir);
            Process.Start(psi)?.Dispose();
        }
    }
}
#endif
