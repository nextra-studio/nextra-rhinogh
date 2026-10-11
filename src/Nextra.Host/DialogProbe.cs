using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace Nextra.Host
{
    /// Detects the dialog blocking Rhino, reads its text, and can close it. Runs on the server thread and
    /// never calls into the Rhino UI thread (that thread may be busy). Never sends keystrokes (Esc would also
    /// interrupt a running GH solution). WPF/Eto dialogs have no child HWNDs, so their text is read through
    /// UI Automation on a helper thread with a time limit.
    static class DialogProbe
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        // InternalGetWindowText, not GetWindowText: for windows of our own process GetWindowText SENDS WM_GETTEXT
        // and waits for the (possibly busy) UI thread — that made timeouts and status hang during long runs
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int InternalGetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111, GW_OWNER = 4;
        const int IDCANCEL = 2;

        /// Rhino's main window, cached on the UI thread at start (RhinoApp.MainWindowHandle() marshals to the UI thread).
        public static IntPtr MainHandle;

        static string Text(IntPtr h) { var t = new StringBuilder(512); InternalGetWindowText(h, t, 512); return t.ToString(); }
        static string Cls(IntPtr h) { var c = new StringBuilder(128); GetClassName(h, c, 128); return c.ToString(); }

        static IntPtr Main()
        {
            if (MainHandle != IntPtr.Zero && IsWindow(MainHandle)) return MainHandle;
            try { return System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        /// Blocking popups: (a) the last active popup of each disabled top-level window (a modal's owner is disabled),
        /// (b) visible top-level windows that look like message boxes (plug-in boxes are often modeless but still stop work).
        static List<IntPtr> Modals()
        {
            var res = new List<IntPtr>();
            uint me = (uint)Environment.ProcessId;
            var main = Main();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || !IsWindowVisible(h)) return true;
                if (!IsWindowEnabled(h))
                {
                    var pop = GetLastActivePopup(h);
                    if (pop != IntPtr.Zero && pop != h && IsWindowVisible(pop) && !res.Contains(pop)) res.Add(pop);
                    return true;
                }
                if (h == main || res.Contains(h)) return true;
                var title = Text(h);
                if (title.Length == 0 || title.StartsWith("Grasshopper") || title.Contains(" - Rhino 8")) return true;
                bool boxClass = Cls(h) == "#32770";
                bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
                if (boxClass || (owned && Regex.IsMatch(title, "error|warning|problem|exception|save|confirm|license|update", RegexOptions.IgnoreCase)))
                    res.Add(h);
                return true;
            }, IntPtr.Zero);
            return res;
        }

        static List<string> ChildTexts(IntPtr dlg)
        {
            var parts = new List<string>();
            EnumChildWindows(dlg, (c, l) =>
            {
                if (!IsWindowVisible(c)) return true;
                var t = Text(c).Trim();
                if (t.Length > 1 && !parts.Contains(t)) parts.Add(t);
                return true;
            }, IntPtr.Zero);
            return parts;
        }

        /// UI Automation names of a WPF/Eto window: (texts, buttons). Runs on a helper thread; if Rhino's UI thread
        /// does not answer within 1.5 s we give up rather than hang the server.
        static (List<string> texts, List<string> buttons) Uia(IntPtr h)
        {
            var texts = new List<string>();
            var buttons = new List<string>();
            var t = new Thread(() =>
            {
                try
                {
                    var e = System.Windows.Automation.AutomationElement.FromHandle(h);
                    var all = e.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                    foreach (System.Windows.Automation.AutomationElement x in all)
                    {
                        var n = (x.Current.Name ?? "").Trim();
                        if (n.Length == 0) continue;
                        var list = x.Current.ControlType == System.Windows.Automation.ControlType.Button ? buttons : texts;
                        lock (list) if (!list.Contains(n)) list.Add(n);
                    }
                }
                catch { }
            }) { IsBackground = true };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
            if (!t.Join(1500)) return (new List<string>(), new List<string>());
            lock (texts) { lock (buttons) { texts.RemoveAll(buttons.Contains); return (texts.ToList(), buttons.ToList()); } }
        }

        static string Describe1(IntPtr h)
        {
            var title = Text(h);
            var body = ChildTexts(h);
            var buttons = new List<string>();
            if (body.Count == 0) (body, buttons) = Uia(h);
            var s = string.Join(" | ", body);
            if (s.Length > 400) s = s.Substring(0, 400) + "…";
            return $"'{(title.Length > 0 ? title : Cls(h))}'" + (s.Length > 0 ? ": " + s : "") + (buttons.Count > 0 ? " [buttons: " + string.Join(", ", buttons) + "]" : "");
        }

        public static string Describe()
        {
            try
            {
                var m = Modals();
                if (m.Count == 0) return null;
                return "Modal dialog open: " + string.Join(" ; ", m.Select(Describe1));
            }
            catch (Exception e) { return "dialog probe failed: " + e.Message; }
        }

        static void Close(IntPtr h)
        {
            if (Cls(h) == "#32770") PostMessage(h, WM_COMMAND, (IntPtr)IDCANCEL, IntPtr.Zero);
            PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        /// Close dialogs whose title matches config "auto_dismiss" (default: Karamba's EPPlus compatibility notice).
        public static string AutoDismiss()
        {
            var list = new List<string>();
            try
            {
                var p = System.IO.Path.Combine(Server.AppDir, "config.json");
                var cfg = System.IO.File.Exists(p) ? JsonNode.Parse(System.IO.File.ReadAllText(p)) as JsonObject : null;
                if (cfg?["auto_dismiss"] is JsonArray arr) foreach (var x in arr) list.Add((string)x);
                else list.Add("Karamba3D compatibility problem");
            }
            catch { list.Add("Karamba3D compatibility problem"); }
            var closed = new List<string>();
            foreach (var h in Modals())
            {
                var t = Text(h);
                if (!list.Any(x => t.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                Close(h);
                closed.Add(t);
            }
            return closed.Count == 0 ? null : "auto-dismissed: " + string.Join(", ", closed);
        }

        /// Cancel/close every blocking dialog, then check it is really gone. A dialog that ignores the request is reported.
        public static string Dismiss()
        {
            var m = Modals();
            if (m.Count == 0) return "nothing dismissed";
            var names = m.ConvertAll(h => Text(h).Length > 0 ? Text(h) : Cls(h));
            foreach (var h in m) Close(h);
            var stuck = new List<string>();
            for (int i = 0; i < 10; i++)
            {
                Thread.Sleep(150);
                stuck = m.Where(h => IsWindow(h) && IsWindowVisible(h)).Select(Text).ToList();
                if (stuck.Count == 0) break;
            }
            return stuck.Count == 0 ? $"closed {m.Count} dialog(s): " + string.Join(", ", names)
                : $"asked {m.Count} dialog(s) to close; still open: " + string.Join(", ", stuck) + " — ask the user to answer it in Rhino";
        }
    }
}
