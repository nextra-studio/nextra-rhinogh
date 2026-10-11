using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
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

            Pending p;
            lock (Gate)
            {
                if (_running != null && !_running.Done)
                {
                    var dlg = DialogProbe.Describe();
                    return Reply(false, $"busy: previous '{_running.Tool}' still running ({(DateTime.Now - _running.Start).TotalSeconds:0}s). " +
                        (dlg ?? "No dialog detected: probably a long computation.") +
                        " Wait and call again, or query {scope:'status', dismiss_dialog:true} to press Esc on a dialog.");
                }
                p = new Pending { Tool = tool };
                _running = p;
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
                        Late.Add($"[late] '{tool}' finished after {(DateTime.Now - p.Start).TotalSeconds:0}s:\n" + TextOf(res, 1500));
                }
                p.Event.Set();
            }));

            if (!p.Event.Wait(timeoutMs))
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
            return AttachLate(p.Result);
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

    /// Detects modal dialogs owned by this Rhino process (Win32), and can press Esc on them.
    static class DialogProbe
    {
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        const uint GW_OWNER = 4, WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_CLOSE = 0x0010;

        static List<(IntPtr h, string title, string cls)> Dialogs()
        {
            var list = new List<(IntPtr, string, string)>();
            uint me = (uint)Environment.ProcessId;
            IntPtr main = IntPtr.Zero;
            try { main = RhinoApp.MainWindowHandle(); } catch { }
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me || h == main || !IsWindowVisible(h)) return true;
                var t = new StringBuilder(256); GetWindowText(h, t, 256);
                var c = new StringBuilder(128); GetClassName(h, c, 128);
                string title = t.ToString(), cls = c.ToString();
                bool owned = GetWindow(h, GW_OWNER) != IntPtr.Zero;
                // modal candidates: classic dialogs, or owned windows while the main window is disabled
                if (cls == "#32770" || (owned && main != IntPtr.Zero && !IsWindowEnabled(main)))
                    list.Add((h, title, cls));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static string Describe()
        {
            try
            {
                var d = Dialogs();
                IntPtr main = IntPtr.Zero;
                try { main = RhinoApp.MainWindowHandle(); } catch { }
                bool mainDisabled = main != IntPtr.Zero && !IsWindowEnabled(main);
                if (d.Count == 0) return mainDisabled ? "Rhino main window is disabled (a modal dialog is probably open)." : null;
                var parts = new List<string>();
                foreach (var x in d) parts.Add($"'{(x.title.Length > 0 ? x.title : "(untitled)")}'");
                return "Dialog open: " + string.Join(", ", parts) + (mainDisabled ? " (modal)" : "");
            }
            catch (Exception e) { return "dialog probe failed: " + e.Message; }
        }

        public static string Dismiss()
        {
            var d = Dialogs();
            foreach (var x in d)
            {
                PostMessage(x.h, WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
                PostMessage(x.h, WM_KEYUP, (IntPtr)0x1B, IntPtr.Zero);
                PostMessage(x.h, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            }
            return d.Count == 0 ? "nothing dismissed" : $"sent Esc/close to {d.Count} dialog(s)";
        }
    }
}
