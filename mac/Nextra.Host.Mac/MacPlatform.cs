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
        public static void RoundCorners(Eto.Forms.Window w) { }   // macOS rounds windows itself

        /// Windows hands the drag to the system (WindowsQuirks.cs); on Mac the page moves the window by deltas (moveBy).
        public static bool SystemDrag(Eto.Forms.Window w, bool resize) => false;

        /// Windows only: Rhino took Ctrl+C / V from WebView2 there. WKWebView gets Cmd+C / V through the menu as usual.
        public static void KeepEditKeys(Eto.Forms.WebView web) { }

        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib")] static extern IntPtr sel_registerName(string name);
        [System.Runtime.InteropServices.DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")] static extern void SendBool(IntPtr receiver, IntPtr selector, bool value);

        /// Clicks pass through the window (Nexiy while it roams): NSWindow setIgnoresMouseEvents:.
        public static void ClickThrough(Eto.Forms.Window w, bool on)
        {
            try
            {
                var h = w.NativeHandle;       // Eto.Mac: the NSWindow
                if (h != IntPtr.Zero) SendBool(h, sel_registerName("setIgnoresMouseEvents:"), on);
            }
            catch { }
        }

        /// The assistant window's translucency on Mac: the window's own alpha (NSWindow.alphaValue through Eto), which
        /// WKWebView draws with. Windows uses the acrylic backdrop instead (WindowsQuirks.cs). "alpha": the page stays solid.
        public static string Backdrop(Eto.Forms.Window w, Eto.Forms.WebView web, bool dark)
        {
            try { w.Opacity = 0.94; return "alpha"; } catch { return "none"; }
        }

        /// The assistant's snip on Mac: the system's own interactive capture (crosshair, drag a box, Esc cancels), as the
        /// Windows overlay does it (Snip.cs). Blocks until the person is done (a few seconds). null = cancelled.
        public static byte[] NativeSnip(out bool handled)
        {
            handled = true;
            var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexiy-snip-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
            try
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/screencapture", $"-i -x \"{file}\"") { UseShellExecute = false });
                p?.WaitForExit(120000);
                return System.IO.File.Exists(file) ? System.IO.File.ReadAllBytes(file) : null;
            }
            catch { return null; }
            finally { try { System.IO.File.Delete(file); } catch { } }
        }
        public static (byte[] png, System.Drawing.Rectangle phys)? GrabScreen(IntPtr rhinoWindow) => null;
        public static System.Collections.Generic.List<(byte[] png, System.Drawing.Rectangle phys, System.Drawing.Rectangle rhino)> GrabScreens() => new System.Collections.Generic.List<(byte[], System.Drawing.Rectangle, System.Drawing.Rectangle)>();
        public static void Foreground(Eto.Forms.Window w) { try { w.BringToFront(); } catch { } }
        public static System.Drawing.Point? CursorScreen() => null;
        public static bool EscDown() => false;
        public static bool AppInFront() => Eto.Forms.Application.Instance.IsActive;
        public static void PlaceOn(Eto.Forms.Window w, System.Drawing.Rectangle r) { }
        public static byte[] CropPng(byte[] png, int x, int y, int w, int h) => null;
    }

    /// Secret store for KeyStore (the person's AI keys): the login Keychain, service "NextraRhinoGh", via /usr/bin/security.
    /// Windows uses DPAPI (src/Nextra.Host/WindowsSecrets.cs). Same three calls on both.
    static class PlatformSecrets
    {
        const string Service = "NextraRhinoGh";

        public static string Read(string name)
        {
            var (code, output) = Security("find-generic-password", "-s", Service, "-a", name, "-w");
            return code == 0 ? output.TrimEnd('\n', '\r') : null;
        }

        public static void Write(string name, string value)
        {
            // -U updates an existing item. The value is an argument of the security tool (briefly visible to this user's own
            // processes); the Keychain then holds it encrypted.
            var (code, output) = Security("add-generic-password", "-U", "-s", Service, "-a", name, "-w", value);
            if (code != 0) throw new InvalidOperationException("the Keychain refused the key: " + output.Trim());
        }

        public static void Delete(string name) => Security("delete-generic-password", "-s", Service, "-a", name);

        static (int, string) Security(params string[] args)
        {
            var psi = new ProcessStartInfo("/usr/bin/security") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var o = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, o);
        }
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
