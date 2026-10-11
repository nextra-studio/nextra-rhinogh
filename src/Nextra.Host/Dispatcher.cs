using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Text.RegularExpressions;
using Rhino;

namespace Nextra.Host
{
    /// Runs one engine call at a time on the Rhino UI thread, with a timeout.
    /// A call that times out keeps running; its result is delivered with the next reply ("late").
    static class Dispatcher
    {
        class Pending
        {
            public string Tool;
            public DateTime Start = DateTime.Now;
            public string Result;
            public volatile bool Done, TimedOut;
            public readonly ManualResetEventSlim Event = new ManualResetEventSlim(false);
        }

        static readonly object Gate = new object();
        static Pending _running;
        static readonly List<string> Late = new List<string>();

        public static string BusyText()
        {
            lock (Gate)
            {
                if (_running == null || _running.Done) return null;
                return $"'{_running.Tool}' running for {(DateTime.Now - _running.Start).TotalSeconds:0}s";
            }
        }

        public static string Call(string tool, string argsJson, int timeoutMs)
        {
            // status is answered from the server thread so it works while the UI thread is blocked
            if (tool == "query" && IsStatusQuery(argsJson, out bool dismiss)) return Status(dismiss);

            // parallel calls from one client are queued (one at a time on the UI thread);
            // only a call stuck past its own timeout makes later calls fail fast
            Pending p;
            var deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (true)
            {
                Pending prev;
                lock (Gate)
                {
                    prev = _running;
                    if (prev == null || prev.Done)
                    {
                        p = new Pending { Tool = tool };
                        _running = p;
                        break;
                    }
                    if (prev.TimedOut)
                    {
                        var dlg = DialogProbe.Describe();
                        return Reply(false, $"busy: earlier '{prev.Tool}' still running ({(DateTime.Now - prev.Start).TotalSeconds:0}s, past its timeout). " +
                            (dlg ?? "No dialog detected: long computation.") +
                            " Wait and call again; query {scope:'status'} shows progress; dismiss_dialog:true cancels a blocking dialog.");
                    }
                }
                var left = deadline - DateTime.Now;
                if (left <= TimeSpan.Zero) return Reply(false, $"timeout: waited for earlier '{prev.Tool}' to finish; retry");
                prev.Event.Wait(left);
            }

            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                string res;
                try
                {
                    EngineLoader.EnsureFresh();
                    res = EngineLoader.Call(tool, argsJson);
                }
                catch (Exception e)
                {
                    res = Reply(false, "engine load/call failed: " + (e.InnerException ?? e).Message);
                }
                lock (Gate)
                {
                    p.Result = res;
                    p.Done = true;
                    if (p.TimedOut)
                        Late.Add($"[late] '{tool}' finished after {(DateTime.Now - p.Start).TotalSeconds:0}s:\n" + TextOf(res, 6000));
                }
                p.Event.Set();
            }));

            // wait in 1 s slices so known-harmless notices (config "auto_dismiss") can be closed while we wait
            var until = DateTime.Now.AddMilliseconds(timeoutMs);
            var autoNotes = new List<string>();
            while (!p.Event.Wait(1000))
            {
                if (DateTime.Now >= until) break;
                var closed = DialogProbe.AutoDismiss();
                if (closed != null) autoNotes.Add(closed);
            }
            if (!p.Done)
            {
                lock (Gate)
                {
                    if (!p.Done)
                    {
                        p.TimedOut = true;
                        var dlg = DialogProbe.Describe();
                        return Reply(false, $"timeout after {timeoutMs / 1000}s: '{tool}' is still running on the Rhino UI thread. " +
                            (dlg ?? "No dialog detected: long computation or infinite loop.") +
                            " Its result will be attached to your next call. Next: query {scope:'status'} to check; if a dialog blocks, query {scope:'status', dismiss_dialog:true}.");
                    }
                }
            }
            var res0 = AttachLate(p.Result);
            if (autoNotes.Count == 0) return res0;
            try
            {
                var j = JsonNode.Parse(res0) as JsonObject;
                j["text"] = (string)j["text"] + "\n(" + string.Join("; ", autoNotes) + ")";
                return j.ToJsonString();
            }
            catch { return res0; }
        }

        public static string Reload()
        {
            var done = new ManualResetEventSlim(false);
            string msg = null;
            RhinoApp.InvokeOnUiThread(new Action(() =>
            {
                try { EngineLoader.ForceReload(); msg = Reply(true, "engine reloaded: " + EngineLoader.Describe()); }
                catch (Exception e) { msg = Reply(false, "reload failed: " + (e.InnerException ?? e).Message); }
                done.Set();
            }));
            return done.Wait(30000) ? msg : Reply(false, "reload timed out: UI thread busy. " + (DialogProbe.Describe() ?? ""));
        }

        static bool IsStatusQuery(string argsJson, out bool dismiss)
        {
            dismiss = false;
            try
            {
                var j = JsonNode.Parse(argsJson);
                if ((string)j?["scope"] != "status") return false;
                dismiss = j?["dismiss_dialog"] != null && j["dismiss_dialog"].GetValue<bool>();
                return true;
            }
            catch { return false; }
        }

        static string Status(bool dismiss)
        {
            var sb = new StringBuilder();
            sb.Append("engine ").Append(EngineLoader.Describe());
            var busy = BusyText();
            sb.Append(busy == null ? "; idle" : "; busy: " + busy);
            var dlg = DialogProbe.Describe();
            if (dlg != null)
            {
                sb.Append("\n").Append(dlg);
                if (dismiss) sb.Append("\n").Append(DialogProbe.Dismiss());
            }
            else if (dismiss) sb.Append("\nno dialog to dismiss");
            lock (Gate)
            {
                foreach (var l in Late) sb.Append("\n").Append(l);
                Late.Clear();
            }
            return Reply(true, sb.ToString());
        }

        static string AttachLate(string res)
        {
            lock (Gate)
            {
                if (Late.Count == 0) return res;
                try
                {
                    var j = JsonNode.Parse(res) as JsonObject;
                    j["text"] = string.Join("\n", Late) + "\n---\n" + (string)j["text"];
                    Late.Clear();
                    return j.ToJsonString();
                }
                catch { return res; }
            }
        }

        static string TextOf(string json, int max)
        {
            try
            {
                var t = (string)JsonNode.Parse(json)?["text"] ?? "";
                return t.Length > max ? t.Substring(0, max) + "…" : t;
            }
            catch { return json; }
        }

        public static string Reply(bool ok, string text) => JsonSerializer.Serialize(new { ok, text });
    }

    /// Detects the modal dialog blocking Rhino (the main window's last active popup), reads its text,
    /// and can cancel it. Never sends keystrokes (Esc would also interrupt a running GH solution).
    static class DialogProbe
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetLastActivePopup(IntPtr h);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        const uint WM_CLOSE = 0x0010, WM_COMMAND = 0x0111;
        const int IDCANCEL = 2;

        static string Text(IntPtr h) { var t = new StringBuilder(512); GetWindowText(h, t, 512); return t.ToString(); }
        static string Cls(IntPtr h) { var c = new StringBuilder(128); GetClassName(h, c, 128); return c.ToString(); }

        /// Blocking popups: (a) last active popup of each disabled top-level window, (b) any other visible,
        /// titled top-level window of this process that is not Rhino's main window or the GH editor
        /// (plug-in message boxes are often non-modal top-levels that still stop the script).
        static List<IntPtr> Modals()
        {
            var res = new List<IntPtr>();
            uint me = (uint)Environment.ProcessId;
            IntPtr main = IntPtr.Zero;
            try { main = RhinoApp.MainWindowHandle(); } catch { }
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
                var title = Text(h);
                if (h != main && title.Length > 0 && !title.StartsWith("Grasshopper") && !title.Contains("Rhino 8") && !res.Contains(h)
                    && (Cls(h) == "#32770" || Regex.IsMatch(title, "error|warning|problem|exception|save|confirm|license|update", RegexOptions.IgnoreCase)))
                    res.Add(h);
                return true;
            }, IntPtr.Zero);
            return res;
        }

        static string Body(IntPtr dlg)
        {
            var parts = new List<string>();
            EnumChildWindows(dlg, (c, l) =>
            {
                if (!IsWindowVisible(c)) return true;
                var t = Text(c).Trim();
                if (t.Length > 1 && !parts.Contains(t)) parts.Add(t);
                return true;
            }, IntPtr.Zero);
            var s = string.Join(" | ", parts);
            return s.Length > 400 ? s.Substring(0, 400) + "…" : s;
        }

        public static string Describe()
        {
            try
            {
                var m = Modals();
                if (m.Count == 0) return null;
                var parts = new List<string>();
                foreach (var h in m) parts.Add($"'{Text(h)}': {Body(h)}");
                return "Modal dialog open: " + string.Join(" ; ", parts);
            }
            catch (Exception e) { return "dialog probe failed: " + e.Message; }
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
                if (Cls(h) == "#32770") PostMessage(h, WM_COMMAND, (IntPtr)IDCANCEL, IntPtr.Zero);
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                closed.Add(t);
            }
            return closed.Count == 0 ? null : "auto-dismissed: " + string.Join(", ", closed);
        }

        public static string Dismiss()
        {
            var m = Modals();
            foreach (var h in m)
            {
                if (Cls(h) == "#32770") PostMessage(h, WM_COMMAND, (IntPtr)IDCANCEL, IntPtr.Zero);
                PostMessage(h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            return m.Count == 0 ? "nothing dismissed" : $"cancelled/closed {m.Count} dialog(s): " + string.Join(", ", m.ConvertAll(Text));
        }
    }
}
