using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;
using Rhino;
using Rhino.Runtime.Code;
using Rhino.Runtime.Code.Execution;
using Rhino.Runtime.Code.Languages;

namespace Nextra.Engine
{
    /// `run`: execute Python 3 / C# inside Rhino via RhinoCode.
    /// Sources: code | patch (find/replace on the last code) | file | lib (saved library script).
    /// Every run: checkpoint, Rhino delta, GH delta (if GH touched), stdout, and on error the failing
    /// line with its neighbours, so a fix can be sent as a small patch.
    public static class RunTool
    {
        const string PyPrelude =
"import System, Rhino, scriptcontext as sc, Rhino.Geometry as rg\n" +
"doc = sc.doc\n" +
"_nx = System.AppDomain.CurrentDomain\n" +
"def h(x): return _nx.GetData('nextra.resolve').Invoke(str(x))\n" +
"def handle(o): return _nx.GetData('nextra.handle').Invoke(o)\n" +
"def ghdoc(): return _nx.GetData('nextra.ghdoc').Invoke()\n" +
"import json as _json; args = _json.loads(str(_nx.GetData('nextra.args') or '{}'))\n";

        const string CsPrelude =
"using System; using System.Linq; using System.Collections.Generic; using Rhino; using Rhino.Geometry; using Rhino.DocObjects;\n" +
"var doc = RhinoDoc.ActiveDoc;\n" +
"object h(string x) => ((Func<string, object>)AppDomain.CurrentDomain.GetData(\"nextra.resolve\"))(x);\n" +
"string handle(object o) => ((Func<object, string>)AppDomain.CurrentDomain.GetData(\"nextra.handle\"))(o);\n" +
"object ghdoc() => ((Func<object>)AppDomain.CurrentDomain.GetData(\"nextra.ghdoc\"))();\n" +
"var args = (string)AppDomain.CurrentDomain.GetData(\"nextra.args\") ?? \"{}\";\n";

        public static void Run(Args a, Reply r)
        {
            var lang = (a.Str("lang", "py") ?? "py").ToLowerInvariant();
            if (lang == "python" || lang == "python3" || lang == "py3") lang = "py";
            if (lang == "c#" || lang == "csharp") lang = "cs";
            if (lang != "py" && lang != "cs") throw new NxError($"lang '{lang}' unsupported", "lang: py | cs");

            string code = Source(a, lang, out string origin);
            string label = a.Str("label", null) ?? FirstLine(code);
            var argsJson = a.Raw("args")?.ToJsonString() ?? "{}";
            Entry.State["run.last." + lang] = code;

            var res = Execute(code, lang, argsJson, label, r);
            if (res.ok)
            {
                var save = a.Str("save_as", null);
                if (!string.IsNullOrEmpty(save)) r.Line(Library.Save(save, code, lang, a.Str("doc", null)));
            }
            if (origin != null) r.Note(origin);
        }

        static string FirstLine(string code)
        {
            var l = code.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !x.StartsWith("import") && !x.StartsWith("using")) ?? "run";
            return Fmt.Trunc(l.TrimStart('#', '/', ' '), 40);
        }

        static string Source(Args a, string lang, out string origin)
        {
            origin = null;
            if (a.Has("code")) return a.Str("code").Replace("\r\n", "\n");
            if (a.Has("patch"))
            {
                if (!Entry.State.TryGetValue("run.last." + lang, out var last) || !(last is string lastCode))
                    throw new NxError("no previous code to patch for lang " + lang, "send full `code` first");
                var patched = Patch.Apply(lastCode, a.Arr("patch"));
                origin = "patched previous code";
                return patched;
            }
            if (a.Has("file"))
            {
                var p = a.Str("file");
                if (!File.Exists(p)) throw new NxError($"file not found: {p}");
                origin = "file " + p;
                return File.ReadAllText(p).Replace("\r\n", "\n");
            }
            if (a.Has("lib"))
            {
                var p = Library.Find(a.Str("lib"), out var lang2);
                origin = "library " + p;
                return File.ReadAllText(p).Replace("\r\n", "\n");
            }
            throw new NxError("nothing to run", "give one of: code, patch [{find,replace}], file, lib");
        }

        public class Result { public bool ok; public string output; }

        public static Result Execute(string code, string lang, string argsJson, string label, Reply r)
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new NxError("no active Rhino document");
            bool touchesGh = Regex.IsMatch(code, @"Grasshopper|ghdoc\(|h\(\s*['""]g\d", RegexOptions.IgnoreCase);
            var ghBefore = touchesGh ? GhCore.Snapshot() : null;
            var ck = Checkpoints.Begin("run: " + label, touchesGh);

            SetHelpers(argsJson);
            string prelude = lang == "py" ? PyPrelude : CsPrelude;
            int preludeLines = prelude.Count(ch => ch == '\n');
            string full = prelude + code;

            var output = new MemoryStream();
            var error = new MemoryStream();
            Exception failure = null;
            uint undoSn = doc.BeginUndoRecord("Nextra " + label);
            var delta = new DeltaTracker(doc);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var spec = lang == "py" ? LanguageSpec.Python3 : LanguageSpec.CSharp;
                var language = Language(spec);
                var c = language.CreateCode(full);
                var ctx = new RunContext("nextra", false, false)
                {
                    OutputStream = output,
                    ErrorStream = error,
                    AutoApplyParams = true,
                };
                c.Run(ctx);
            }
            catch (Exception e) { failure = e; }
            finally
            {
                delta.Dispose();
                doc.EndUndoRecord(undoSn);
                doc.Views.Redraw();
            }
            sw.Stop();
            Checkpoints.Complete(ck, delta);

            string outText = Decode(output);
            string errText = Decode(error);
            bool ok = failure == null && string.IsNullOrWhiteSpace(errText.Replace("Warning", ""));
            if (failure == null && !string.IsNullOrWhiteSpace(errText) && !Regex.IsMatch(errText, "Error|Exception|Traceback")) ok = true;

            r.Ok = ok;
            r.Line($"{(ok ? "ok" : "FAILED")} {lang} {sw.ElapsedMilliseconds}ms, checkpoint {ck["id"]}");
            if (!string.IsNullOrWhiteSpace(outText)) r.Line("stdout:\n" + Clip(outText, 2500));
            if (!ok)
            {
                var msg = ErrorText(failure, errText);
                r.Line("error: " + Clip(msg, 1200));
                var lines = ErrorLines(msg, failure, preludeLines);
                if (lines.Count > 0) r.Line(Context(code, lines));
                Memory.AutoLog("run." + lang, Signature(msg));
                if (!delta.Empty) r.Line("note: partial changes were made before the error (undo " + ck["id"] + " reverts them)");
            }
            delta.Report(r);
            if (ghBefore != null) GhCore.ReportDelta(ghBefore, r);
            if (delta.Empty && (ghBefore == null || !GhCore.Changed(ghBefore)) && ok) Checkpoints.Discard(ck);
            var hint = Memory.RecurringHint();
            if (hint != null) r.Line(hint);
            return new Result { ok = ok, output = outText };
        }

        /// RhinoCode languages register when the RhinoCode plug-in loads; load it silently if needed.
        static ILanguage Language(LanguageSpec spec)
        {
            var l = RhinoCode.Languages.QueryLatest(spec);
            if (l != null) return l;
            foreach (var kv in Rhino.PlugIns.PlugIn.GetInstalledPlugIns())
                if (kv.Value.StartsWith("RhinoCodePlugin", StringComparison.OrdinalIgnoreCase) || kv.Value.Equals("RhinoCode", StringComparison.OrdinalIgnoreCase))
                    Rhino.PlugIns.PlugIn.LoadPlugIn(kv.Key);
            try { RhinoCode.Languages.WaitStatusComplete(spec); } catch { }
            l = RhinoCode.Languages.QueryLatest(spec);
            if (l != null) return l;
            var known = string.Join(", ", Rhino.PlugIns.PlugIn.GetInstalledPlugIns().Values.Where(n => n.IndexOf("code", StringComparison.OrdinalIgnoreCase) >= 0));
            throw new NxError($"RhinoCode language {spec} not available (plug-ins: {known})", "run the ScriptEditor command once so Rhino initialises Python 3 / C#, then retry");
        }

        static void SetHelpers(string argsJson)
        {
            var dom = AppDomain.CurrentDomain;
            dom.SetData("nextra.args", argsJson);
            dom.SetData("nextra.resolve", new Func<string, object>(Resolve));
            dom.SetData("nextra.handle", new Func<object, string>(HandleOf));
            dom.SetData("nextra.ghdoc", new Func<object>(() => GhCore.Doc(true)));
        }

        public static object Resolve(string h)
        {
            h = (h ?? "").Trim();
            if (Handles.IsGhHandle(h))
            {
                var gd = GhCore.Doc(false) ?? throw new Exception("Grasshopper document not open");
                if (!Handles.TryGh(gd.DocumentID, h, out var gg)) throw new Exception($"unknown GH handle {h}");
                return gd.FindObject(gg, true) ?? throw new Exception($"GH object {h} no longer exists");
            }
            if (!Handles.TryRh(h, out var g)) throw new Exception($"unknown handle '{h}'");
            return RhinoDoc.ActiveDoc.Objects.FindId(g) ?? throw new Exception($"object {h} no longer exists (deleted?)");
        }

        public static string HandleOf(object o)
        {
            switch (o)
            {
                case Guid g: return RhinoDoc.ActiveDoc.Objects.FindId(g) != null ? Handles.Rh(g) : GhCore.HandleOfGuid(g) ?? Handles.Rh(g);
                case Rhino.DocObjects.RhinoObject ro: return Handles.Rh(ro.Id);
                case Grasshopper.Kernel.IGH_DocumentObject go: return GhCore.Handle(go);
                default:
                    if (o != null && Guid.TryParse(o.ToString(), out var g2)) return HandleOf(g2);
                    return null;
            }
        }

        static string Decode(MemoryStream ms)
        {
            try { return Encoding.UTF8.GetString(ms.ToArray()).Replace("\r\n", "\n").TrimEnd(); } catch { return ""; }
        }

        public static string Clip(string s, int max)
        {
            if (s.Length <= max) return s;
            int head = max * 2 / 3, tail = max - head;
            return s.Substring(0, head) + $"\n…[{s.Length - max} chars cut]…\n" + s.Substring(s.Length - tail);
        }

        static string ErrorText(Exception e, string err)
        {
            var sb = new StringBuilder();
            if (e != null)
            {
                var x = e;
                while (x.InnerException != null && (x is System.Reflection.TargetInvocationException || x.Message.Length < 5)) x = x.InnerException;
                sb.Append(x.Message);
                if (x is ExecuteException ee && ee.StackTrace != null && !x.Message.Contains("line")) sb.Append(" | ").Append(Fmt.Trunc(ee.StackTrace, 600));
            }
            if (!string.IsNullOrWhiteSpace(err)) { if (sb.Length > 0) sb.Append('\n'); sb.Append(err.Trim()); }
            var s = sb.ToString();
            // drop noisy frames from the prelude / runtime
            s = Regex.Replace(s, @"\s*File ""[^""]*(site-packages|lib[\\/]python)[^""]*"", line \d+[^\n]*\n?", "");
            return s.Trim();
        }

        static List<int> ErrorLines(string msg, Exception e, int prelude)
        {
            var res = new List<int>();
            if (e is ExecuteException ce)
            {
                try { var ln = ce.Position.LineNumber; if (ln > 0) res.Add(ln - prelude); } catch { }
            }
            foreach (Match m in Regex.Matches(msg ?? "", @"line (\d+)|\((\d+),\d+\)"))
            {
                int n = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) - prelude;
                if (n > 0 && !res.Contains(n)) res.Add(n);
            }
            return res.Where(n => n > 0).Distinct().TakeLast(2).ToList();
        }

        public static string Context(string code, List<int> lines)
        {
            var src = code.Split('\n');
            var sb = new StringBuilder();
            foreach (var ln in lines)
            {
                if (ln < 1 || ln > src.Length) continue;
                for (int i = Math.Max(1, ln - 2); i <= Math.Min(src.Length, ln + 1); i++)
                    sb.AppendLine($"{(i == ln ? ">" : " ")}{i,4}| {src[i - 1]}");
            }
            return sb.ToString().TrimEnd();
        }

        public static string Signature(string msg)
        {
            var first = (msg ?? "").Split('\n').FirstOrDefault(l => Regex.IsMatch(l, "Error|Exception")) ?? (msg ?? "").Split('\n')[0];
            first = Regex.Replace(first, @"'[^']{0,60}'|""[^""]{0,60}""", "'…'");
            first = Regex.Replace(first, @"\d+(\.\d+)?", "N");
            return Fmt.Trunc(first.Trim(), 120);
        }
    }

    /// Exact find/replace patches. Each `find` must match exactly once; failures explain why.
    public static class Patch
    {
        public static string Apply(string src, System.Text.Json.Nodes.JsonArray patches)
        {
            if (patches == null || patches.Count == 0) throw new NxError("patch must be a list of {find, replace}");
            int i = 0;
            foreach (var p in patches)
            {
                i++;
                var find = (string)p?["find"];
                var rep = (string)p?["replace"] ?? "";
                if (string.IsNullOrEmpty(find)) throw new NxError($"patch #{i}: empty find");
                find = find.Replace("\r\n", "\n"); rep = rep.Replace("\r\n", "\n");
                int first = src.IndexOf(find, StringComparison.Ordinal);
                if (first < 0)
                {
                    // whitespace-insensitive retry
                    var rx = new Regex(Regex.Escape(find.Trim()).Replace("\\ ", "\\s+").Replace("\\n", "\\s*\\n\\s*"));
                    var ms = rx.Matches(src);
                    if (ms.Count == 1) { src = src.Substring(0, ms[0].Index) + rep + src.Substring(ms[0].Index + ms[0].Length); continue; }
                    throw new NxError($"patch #{i}: find text not found", "closest line: " + Closest(src, find) + " — copy the exact text, or send full code");
                }
                int second = src.IndexOf(find, first + find.Length, StringComparison.Ordinal);
                if (second >= 0) throw new NxError($"patch #{i}: find text occurs more than once", "include more surrounding text to make it unique");
                src = src.Substring(0, first) + rep + src.Substring(first + find.Length);
            }
            return src;
        }

        static string Closest(string src, string find)
        {
            var key = find.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? find;
            var lines = src.Split('\n');
            int best = -1; int bestScore = int.MaxValue;
            for (int i = 0; i < lines.Length; i++)
            {
                int d = Lev(lines[i].Trim(), key);
                if (d < bestScore) { bestScore = d; best = i; }
            }
            return best < 0 ? "(none)" : $"{best + 1}| {lines[best].Trim()}";
        }

        static int Lev(string a, string b)
        {
            if (a.Length > 200) a = a.Substring(0, 200);
            if (b.Length > 200) b = b.Substring(0, 200);
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return d[a.Length, b.Length];
        }
    }
}
