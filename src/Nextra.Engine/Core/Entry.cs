using System;
using System.Collections.Generic;
using System.IO;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;

namespace Nextra.Engine
{
    /// Called by Nextra.Host on the Rhino UI thread. Every call returns JSON {ok, text, images[]}:
    /// text is what the LLM reads, so it is always dense and actionable, never a raw stack trace.
    public static class Entry
    {
        public static IDictionary<string, object> State;
        public static string AppDir => State != null && State.TryGetValue("host.appdir", out var d) ? (string)d
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NextraRhinoGh");

        public static void Init(IDictionary<string, object> state)
        {
            State = state;
        }

        public static void Shutdown()
        {
            try { DeltaTracker.DetachAll(); } catch { }
        }

        public static string Call(string tool, string argsJson, IDictionary<string, object> state)
        {
            State = state;
            var r = new Reply();
            Args a;
            try { a = Args.Parse(argsJson); }
            catch (Exception e)
            {
                r.Fail("args are not valid JSON: " + e.Message);
                return r.ToJson();
            }
            try
            {
                switch (tool)
                {
                    case "view": ViewTool.Run(a, r); break;
                    case "query": QueryTool.Run(a, r); break;
                    case "run": RunTool.Run(a, r); break;
                    case "gh_edit": GhEditTool.Run(a, r); break;
                    case "undo": UndoTool.Run(a, r); break;
                    case "memory": MemoryTool.Run(a, r); break;
                    default:
                        r.Fail($"unknown tool '{tool}'. Tools: view, query, run, gh_edit, undo, memory.");
                        break;
                }
                a.ReportUnused(r);
            }
            catch (NxError e)
            {
                r.Fail(e.Message, e.Hint);
            }
            catch (Exception e)
            {
                var root = e is System.Reflection.TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                Log.Write("internal", tool + " " + argsJson + "\n" + root);
                r.Fail($"internal engine error in '{tool}': {root.GetType().Name}: {root.Message}",
                    "Engine bug, not your input. Retry once with simpler args; otherwise fall back to `run` with code.");
            }
            return r.ToJson();
        }
    }

    /// Error with a hint telling the LLM what to do next.
    public class NxError : Exception
    {
        public string Hint;
        public NxError(string msg, string hint = null) : base(msg) { Hint = hint; }
    }

    static class Log
    {
        public static void Write(string kind, string text)
        {
            try
            {
                var p = Path.Combine(Entry.AppDir, "logs");
                Directory.CreateDirectory(p);
                File.AppendAllText(Path.Combine(p, $"engine-{DateTime.Now:yyyyMMdd}.log"), $"[{DateTime.Now:HH:mm:ss}] {kind}: {text}\n");
            }
            catch { }
        }
    }
}
