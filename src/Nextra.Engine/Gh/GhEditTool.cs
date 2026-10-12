using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Nextra.Engine.Gh.Shorthand;
using Nextra.Engine.Rh;
using Rhino;

namespace Nextra.Engine.Gh
{
    /// `gh_edit`: canvas edits. ops = shorthand diff; script = code edit of one script component;
    /// plus open/new/save, bake, layout and solve control. Always checkpointed (GH archive).
    public static class GhEditTool
    {
        public static void Run(Args a, Reply r)
        {
            // document-level actions first
            if (a.Has("open"))
            {
                r.Line(GhCore.OpenFile(a.Str("open"), a.Bool("solve", true), out _, a.Bool("reload", false)));
            }
            if (a.Bool("new", false))
            {
                GhCore.EnsureEditor();
                var nd = new GH_Document();
                Grasshopper.Instances.DocumentServer.AddDocument(nd);
                Grasshopper.Instances.ActiveCanvas.Document = nd;
                r.Line("new GH document");
            }
            var d = GhCore.Require();
            if (a.Has("show"))
            {
                var gh = GhCore.Plugin();
                if (a.Bool("show", true)) gh.ShowEditor(); else gh.HideEditor();
            }

            bool hasOps = a.Has("ops"), hasScript = a.Has("script"), hasBake = a.Has("bake");
            string layout = (a.Str("layout", "auto") ?? "auto").ToLowerInvariant();
            bool mutating = hasOps || hasScript || layout.StartsWith("tidy");
            Dictionary<string, object> ck = null;
            var errBefore = new HashSet<Guid>(GhCore.Messages(d).Where(m => m.level == "error").Select(m => m.o.InstanceGuid));
            if (mutating) ck = Checkpoints.Begin("gh_edit", true);

            var touched = new List<IGH_DocumentObject>();
            BuildResult br = null;
            bool atomic = a.Bool("atomic", false);
            bool solve = a.Bool("solve", true);

            if (hasOps)
            {
                var perr = new List<ParseError>();
                var ops = Parser.Parse(a.Str("ops"), perr);
                if (perr.Count > 0)
                {
                    foreach (var e in perr) r.Line($"L{e.Line}: parse error: {e.Message}");
                    if (atomic || ops.Count == 0)
                    {
                        r.Ok = false;
                        r.Line("nothing applied. Grammar: query {scope:'docs', filter:'shorthand'}");
                        if (ck != null) Checkpoints.Discard(ck);
                        return;
                    }
                }
                bool was = GH_Document.EnableSolutions;
                GH_Document.EnableSolutions = false;   // build everything first, solve once
                try { br = Builder.Apply(d, ops); }
                finally { GH_Document.EnableSolutions = was; }
                if (atomic && br.Errors.Count > 0)
                {
                    GhCore.Restore((byte[])ck["gh"], (Guid)ck["ghdoc"]);
                    Checkpoints.Discard(ck);
                    r.Ok = false;
                    r.Line($"atomic: rolled back, {br.Errors.Count} error(s):");
                    foreach (var e in br.Errors) r.Line(" " + e);
                    return;
                }
                touched.AddRange(br.Touched);
                if (perr.Count > 0) br.Errors.InsertRange(0, perr.Select(e => $"L{e.Line}: parse error: {e.Message}"));
                if (layout != "none" && br.Created.Count > 0 && !layout.StartsWith("tidy"))
                    Layout.Place(d, br.Created.Where(o => !(o is GH_Group)).ToList());
            }

            string scriptReport = null;
            IGH_Component scriptComp = null;
            if (hasScript) scriptReport = EditScript(d, a.Obj("script"), out scriptComp);
            if (scriptComp != null) touched.Add(scriptComp);

            if (layout.StartsWith("tidy"))
            {
                var scope = layout == "tidy" ? d.Objects.ToList() : GhQuery.Select(d, layout.Substring(5), out _);
                if (a.Bool("relays", false))
                {
                    int n = Layout.AddRelays(d, scope.Where(o => !(o is GH_Group)).ToList());
                    r.Line($"relays: {n} boundary params added (<name>_In left / <name>_Out right of each group)");
                    scope = layout == "tidy" ? d.Objects.ToList() : scope.Concat(d.Objects.Where(Layout.IsRelay)).Distinct().ToList();
                }
                r.Line(Layout.Tidy(d, scope));
            }

            long ms = 0;
            if (solve && mutating)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                d.NewSolution(false);
                ms = sw.ElapsedMilliseconds;
            }
            Grasshopper.Instances.ActiveCanvas?.Refresh();

            // ---- report ----
            if (br != null)
            {
                var parts = new List<string>();
                int nodes = br.Created.Count(o => !(o is GH_Group));
                if (nodes > 0) parts.Add($"+{nodes} nodes");
                if (br.Wires > 0) parts.Add($"{br.Wires} wires");
                if (br.Values > 0) parts.Add($"{br.Values} values");
                if (br.GroupsNew > 0) parts.Add($"+{br.GroupsNew} groups");
                if (br.Deleted > 0) parts.Add($"-{br.Deleted} deleted");
                var status = br.Errors.Count == 0 ? "ok" : br.LinesOk == 0 ? "FAILED" : "partial";
                r.Ok = br.Errors.Count == 0;
                r.Line($"{status}: {br.LinesOk}/{br.Lines} lines applied ({string.Join(", ", parts)}){(ck != null ? ", checkpoint " + ck["id"] : "")}");
                var ids = br.IdMap.Where(kv => !kv.Key.StartsWith("## ")).Select(kv => $"{kv.Key}={kv.Value}").ToList();
                if (ids.Count > 0) r.Line("ids: " + string.Join(" ", ids));
                var gids = br.IdMap.Where(kv => kv.Key.StartsWith("## ")).Select(kv => $"{kv.Key.Substring(3)}={kv.Value}").ToList();
                if (gids.Count > 0) r.Line("groups: " + string.Join(" ", gids));
                if (br.Errors.Count > 0)
                {
                    r.Line("errors (fix only these lines; use handles above for nodes that were created):");
                    foreach (var e in br.Errors.Take(20)) r.Line(" " + e);
                    foreach (var e in br.Errors) Memory.AutoLog("gh_edit", RunTool.Signature(Regex.Replace(e, @"^L\d+ `[^`]*`: ", "")));
                }
                foreach (var nnote in br.Notes.Take(8)) r.Line("note: " + nnote);
            }
            if (scriptReport != null) r.Line(scriptReport);

            if (mutating)
            {
                // runtime messages: everything we touched + anything that newly errors elsewhere
                var msgs = GhCore.Messages(d);
                var tset = new HashSet<Guid>(touched.Select(o => o.InstanceGuid));
                var relevant = msgs.Where(m => tset.Contains(m.o.InstanceGuid) || (m.level == "error" && !errBefore.Contains(m.o.InstanceGuid))).ToList();
                r.Line(solve ? $"solved {ms}ms" + (relevant.Count == 0 ? ", no errors/warnings on affected nodes" : ":") : "not solved (solve:false)");
                // "Input parameter X failed to collect data" is a consequence, not a cause: collapse it
                var noData = relevant.Where(m => m.level == "warn" && m.msg.Contains("failed to collect data")).ToList();
                if (noData.Count > 1)
                {
                    relevant = relevant.Except(noData).ToList();
                    r.Line($" no input data downstream ({noData.Select(m => m.o).Distinct().Count()}): {ViewTool.Ranges(noData.Select(m => GhCore.Handle(d, m.o)).Distinct())}");
                }
                foreach (var m in relevant.Take(15))
                {
                    var line = $" {GhCore.Handle(d, m.o)} {m.o.Name} {(m.level == "error" ? "!" : "?")} {Fmt.Trunc(m.msg.Replace("\n", " "), 170)}";
                    r.Line(line);
                    if (m.level == "error") Memory.AutoLog("gh.runtime." + m.o.Name, RunTool.Signature(m.msg));
                    if (Scripts.IsScript(m.o)) { var ctx = ScriptLineContext((IGH_Component)m.o, m.msg); if (ctx != null) r.Line(ctx); }
                }
                // results of the affected sinks
                var sinks = ResultNodes(touched);
                if (solve && sinks.Count > 0)
                {
                    var outs = new List<string>();
                    foreach (var o in sinks)
                    {
                        if (o is IGH_Component c) foreach (var p in c.Params.Output.Where(p => p.NickName != "out").Take(2)) outs.Add($"{GhCore.Handle(d, o)}.{p.NickName}: {Writer.DataSummary(p)}");
                        else if (o is IGH_Param p && !(o is GH_NumberSlider) && !(o is GH_Panel)) outs.Add($"{GhCore.Handle(d, o)}: {Writer.DataSummary(p)}");
                    }
                    if (outs.Count > 0) r.Line("results: " + string.Join(" · ", outs));
                }
                if (a.Bool("echo", false) && touched.Count > 0) r.Line(Writer.Write(d, touched.Distinct(), new WriteOptions { Messages = false }));
                if (ck != null && br != null && br.LinesOk == 0 && br.Deleted == 0 && scriptComp == null) Checkpoints.Discard(ck);
            }

            // plug-in guides: categories of touched nodes + categories named in the ops (@Karamba3D)
            var cats = touched.Select(o => o.Category).ToList();
            if (hasOps) cats.AddRange(Regex.Matches(a.Str("ops") ?? "", @"@([\w\.\- ]+?)(?=[\s(/{]|$)").Select(m => m.Groups[1].Value.Trim()));
            PluginDocs.Check(cats, r, a.Str("plugin_docs", null));

            if (hasBake) Bake(d, a, r);
            if (a.Has("save"))
            {
                // archive write: no GH UI (SaveQuiet shows a modal box on a bad path)
                var path = Path.GetFullPath(a.Str("save"));
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var arc = new GH_IO.Serialization.GH_Archive();
                if (!arc.AppendObject(d, "Definition")) throw new NxError("could not serialise the GH document");
                if (!arc.WriteToFile(path, true, false)) throw new NxError("save failed: " + path, "check the path is writable / not open elsewhere");
                d.FilePath = path;
                r.Line("saved " + path);
            }
            var hint = Memory.RecurringHint();
            if (hint != null) r.Line(hint);
            if (r.Length == 0) r.Line("nothing to do: give ops, script, layout:'tidy', open, new, save or bake");
        }

        static bool IsDisplay(IGH_DocumentObject o) => o is GH_Panel || o is GH_Scribble || o is GH_Group;
        static bool IsInput(IGH_DocumentObject o) => o is GH_NumberSlider || o is GH_BooleanToggle || o is GH_ValueList || o is GH_ColourSwatch || o is GH_Panel;

        /// Where to show the effect of an edit: the touched nodes whose data goes nowhere else that was touched
        /// (display nodes like panels do not count as consumers). If such a node is only an input (slider, toggle…)
        /// or its data flows on to untouched nodes, follow the wires to the end nodes of that branch instead.
        static List<IGH_DocumentObject> ResultNodes(List<IGH_DocumentObject> touched)
        {
            var tset = new HashSet<Guid>(touched.Select(o => o.InstanceGuid));
            var res = new List<IGH_DocumentObject>();
            void Add(IGH_DocumentObject o) { if (!IsDisplay(o) && !IsInput(o) && !res.Contains(o)) res.Add(o); }
            foreach (var o in touched.Where(o => !IsDisplay(o)).Distinct())
            {
                var down = Downstream(o).Where(x => !IsDisplay(x)).Distinct().ToList();
                if (down.Any(x => tset.Contains(x.InstanceGuid))) continue;
                if (down.Count == 0) { Add(o); continue; }
                // walk to the ends of the branch (bounded)
                var seen = new HashSet<Guid>();
                var queue = new Queue<IGH_DocumentObject>(down);
                int guard = 0;
                while (queue.Count > 0 && guard++ < 400)
                {
                    var x = queue.Dequeue();
                    if (!seen.Add(x.InstanceGuid)) continue;
                    var next = Downstream(x).Where(y => !IsDisplay(y)).ToList();
                    if (next.Count == 0) Add(x); else foreach (var y in next) queue.Enqueue(y);
                }
            }
            return res.Take(6).ToList();
        }

        static IEnumerable<IGH_DocumentObject> Downstream(IGH_DocumentObject o)
        {
            var outs = o is IGH_Component c ? c.Params.Output : o is IGH_Param pp ? new List<IGH_Param> { pp } : new List<IGH_Param>();
            foreach (var p in outs) foreach (var rcp in p.Recipients) { var t = rcp.Attributes?.GetTopLevel?.DocObject; if (t != null) yield return t; }
        }

        // ---------- scripts ----------

        static string EditScript(GH_Document d, System.Text.Json.Nodes.JsonObject s, out IGH_Component comp)
        {
            comp = null;
            if (s == null) throw new NxError("script must be an object {handle, code|patch|file|mirror}");
            var a = Args.From(s);
            var h = a.Str("handle", null) ?? throw new NxError("script.handle required (e.g. g7)");
            comp = GhCore.Find(d, h) as IGH_Component;
            if (comp == null || !Scripts.IsScript(comp)) throw new NxError($"{h} is not a script component");
            var cur = Scripts.GetCode(comp);
            string code;
            if (a.Has("code")) code = a.Str("code");
            else if (a.Has("patch")) code = Patch.Apply(cur, a.Arr("patch"));
            else if (a.Has("file")) { var f = a.Str("file"); if (!File.Exists(f)) throw new NxError("file not found: " + f); code = File.ReadAllText(f); }
            else if (a.Bool("from_mirror", false)) { var f = Scripts.MirrorPath(d, comp); if (!File.Exists(f)) throw new NxError("no mirror file " + f, "script {handle, mirror:true} writes it first"); code = File.ReadAllText(f); }
            else if (a.Bool("mirror", false)) return "mirror: " + Scripts.Mirror(d, comp) + " (edit the file, then script {handle, from_mirror:true})";
            else throw new NxError("script needs code | patch [{find,replace}] | file | from_mirror | mirror");
            code = code.Replace("\r\n", "\n");
            var pnote = Scripts.SetCodeSmart(comp, code);
            var mirror = Scripts.Mirror(d, comp);
            int n = code.Split('\n').Length;
            return $"script {h}: {n} lines set ({Scripts.Lang(comp)}){(pnote != null ? "; " + pnote : "")}, mirrored to {Path.GetFileName(mirror)}; ports {ComponentSearch.Describe(comp, false)} | {ComponentSearch.Describe(comp, true)}";
        }

        public static string ScriptLineContext(IGH_Component c, string msg)
        {
            var m = Regex.Match(msg ?? "", @"line (\d+)|\((\d+),\d+\)|\[(\d+):\d+\]");
            if (!m.Success) return null;
            int ln = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value);
            try { return RunTool.Context(Scripts.GetCode(c), new List<int> { ln }); } catch { return null; }
        }

        // ---------- bake ----------

        static void Bake(GH_Document d, Args a, Reply r)
        {
            var doc = RhinoDoc.ActiveDoc;
            var layer = a.Str("layer", null);
            var attrs = new Rhino.DocObjects.ObjectAttributes();
            if (layer != null)
            {
                int li = doc.Layers.FindByFullPath(layer, -1);
                if (li < 0) li = doc.Layers.Add(layer, System.Drawing.Color.Black);
                attrs.LayerIndex = li;
            }
            var ck = Checkpoints.Begin("bake " + string.Join(" ", a.List("bake")), false);
            using var delta = new DeltaTracker(doc);
            foreach (var spec in a.List("bake"))
            {
                var parts = spec.Split('.');
                var o = GhCore.Find(d, parts[0]);
                if (o == null) { r.Line($"bake: {parts[0]} not found"); continue; }
                var ids = new List<Guid>();
                if (parts.Length > 1 && o is IGH_Component c)
                {
                    var p = ComponentSearch.FindPort(c, parts[1], true, out var e) ?? throw new NxError(e);
                    if (p is IGH_BakeAwareObject bp) bp.BakeGeometry(doc, attrs, ids);
                }
                else if (o is IGH_BakeAwareObject bo) bo.BakeGeometry(doc, attrs, ids);
                else { r.Line($"bake: {parts[0]} has nothing bakeable"); continue; }
            }
            doc.Views.Redraw();
            Checkpoints.Complete(ck, delta);
            delta.Report(r);
            r.Line("checkpoint " + ck["id"]);
        }
    }
}
