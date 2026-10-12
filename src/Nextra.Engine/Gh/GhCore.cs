using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Rhino;

namespace Nextra.Engine.Gh
{
    /// Grasshopper document access. Never blocks on UI: the editor is loaded hidden on demand,
    /// files are opened through GH_DocumentIO with solutions disabled until we know what is inside.
    public static class GhCore
    {
        public static Grasshopper.Plugin.GH_RhinoScriptInterface Plugin()
        {
            var o = RhinoApp.GetPlugInObject("Grasshopper") as Grasshopper.Plugin.GH_RhinoScriptInterface;
            if (o == null) throw new NxError("Grasshopper plug-in not available");
            return o;
        }

        public static void EnsureEditor()
        {
            var gh = Plugin();
            if (!gh.IsEditorLoaded()) gh.LoadEditor();
            if (Instances.ActiveCanvas == null) throw new NxError("Grasshopper canvas not available after loading", "run GrasshopperShow once, then retry");
        }

        /// The active GH document. create=true makes one (loading the editor hidden) if none.
        public static GH_Document Doc(bool create)
        {
            bool loaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Grasshopper") && Instances.DocumentServer != null;
            if (!loaded && !create) return null;
            if (create) EnsureEditor();
            var cv = Instances.ActiveCanvas;
            var d = cv?.Document;
            if (d == null && create)
            {
                d = new GH_Document();
                Instances.DocumentServer.AddDocument(d);
                cv.Document = d;
            }
            return d;
        }

        public static GH_Document Require() => Doc(true);

        public static string Handle(IGH_DocumentObject o) => o == null ? null : Handles.Gh(o.OnPingDocument()?.DocumentID ?? Doc(false)?.DocumentID ?? Guid.Empty, o.InstanceGuid);
        public static string Handle(GH_Document d, IGH_DocumentObject o) => Handles.Gh(d.DocumentID, o.InstanceGuid);

        public static string HandleOfGuid(Guid g)
        {
            var d = Doc(false);
            if (d == null) return null;
            var o = d.FindObject(g, true);
            return o == null ? null : Handle(d, o);
        }

        public static IGH_DocumentObject Find(GH_Document d, string handle)
        {
            if (!Handles.TryGh(d.DocumentID, handle, out var g)) return null;
            return d.FindObject(g, true);
        }

        public static string OpenFile(string path, bool solve, out GH_Document doc, bool reload = false)
        {
            if (!System.IO.File.Exists(path)) throw new NxError("file not found: " + path);
            EnsureEditor();
            // already open: switch to it (GH throws when the same file is assigned twice)
            var full = System.IO.Path.GetFullPath(path);
            var open = Instances.DocumentServer.FirstOrDefault(x => x.IsFilePathDefined && string.Equals(System.IO.Path.GetFullPath(x.FilePath), full, StringComparison.OrdinalIgnoreCase));
            if (open != null && !reload)
            {
                if (Instances.ActiveCanvas.Document != open) Instances.ActiveCanvas.Document = open;
                doc = open;
                return $"{System.IO.Path.GetFileName(path)} is already open: switched to it ({open.ObjectCount} objects). reload:true re-reads it from disk";
            }
            if (open != null) { Instances.DocumentServer.RemoveDocument(open); }
            bool wasEnabled = GH_Document.EnableSolutions;   // the user may have solutions switched off: keep that
            GH_Document.EnableSolutions = false;
            var sb = new StringBuilder();
            try
            {
                var io = new GH_DocumentIO();
                if (!io.Open(path) || io.Document == null) throw new NxError("Grasshopper could not read " + path, "file may be corrupt or from a newer version");
                doc = io.Document;
                Instances.DocumentServer.AddDocument(doc);
                Instances.ActiveCanvas.Document = doc;
                var missing = doc.Objects.Where(o => o.GetType().Name.Contains("Missing") || o.GetType().Name.Contains("Placeholder")).Select(o => o.Name).Distinct().ToList();
                sb.Append($"opened {System.IO.Path.GetFileName(path)}: {doc.ObjectCount} objects");
                if (missing.Count > 0) sb.Append($"; MISSING plug-in components: {string.Join(", ", missing.Take(15))}");
            }
            finally { GH_Document.EnableSolutions = wasEnabled; }
            if (solve && !wasEnabled) sb.Append("; solutions are disabled in Grasshopper (not solved)");
            else if (solve)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                doc.NewSolution(false);
                sb.Append($"; solved in {sw.ElapsedMilliseconds}ms");
            }
            else sb.Append("; not solved yet (gh_edit solve:true)");
            return sb.ToString();
        }

        // ---------- checkpoint ----------

        public static byte[] Archive(GH_Document d)
        {
            var a = new GH_Archive();
            a.AppendObject(d, "Definition");
            return a.Serialize_Binary();
        }

        public static string Restore(byte[] bytes, Guid docId)
        {
            var d = Doc(false);
            if (d == null || d.DocumentID != docId)
            {
                d = Instances.DocumentServer?.FirstOrDefault(x => x.DocumentID == docId);
                if (d == null) return "gh: original document is closed; cannot restore";
            }
            var a = new GH_Archive();
            if (!a.Deserialize_Binary(bytes)) return "gh: checkpoint archive unreadable";
            var tmp = new GH_Document();
            a.ExtractObject(tmp, "Definition");
            bool was = GH_Document.EnableSolutions;
            GH_Document.EnableSolutions = false;
            try
            {
                d.RemoveObjects(d.Objects.ToList(), false);
                d.MergeDocument(tmp, false, false);
            }
            finally { GH_Document.EnableSolutions = was; }
            d.NewSolution(false);
            Instances.ActiveCanvas?.Refresh();
            return $"gh: document restored ({d.ObjectCount} objects)";
        }

        // ---------- delta (used by run when code touches GH) ----------

        public static Dictionary<Guid, string> Snapshot()
        {
            var d = Doc(false);
            if (d == null) return new Dictionary<Guid, string>();
            var res = new Dictionary<Guid, string>();
            foreach (var o in d.Objects) res[o.InstanceGuid] = Signature(o);
            res[Guid.Empty] = d.DocumentID.ToString();
            return res;
        }

        static string Signature(IGH_DocumentObject o)
        {
            var sb = new StringBuilder(o.NickName);
            if (o is IGH_Component c)
            {
                foreach (var p in c.Params.Input) sb.Append('|').Append(p.NickName).Append(':').Append(string.Join(",", p.Sources.Select(s => s.InstanceGuid)));
                foreach (var p in c.Params.Output) sb.Append('>').Append(p.NickName);
            }
            else if (o is IGH_Param p) sb.Append('|').Append(string.Join(",", p.Sources.Select(s => s.InstanceGuid)));
            if (o is GH_NumberSlider s) sb.Append('=').Append(s.CurrentValue);
            if (o is GH_Panel pn) sb.Append('=').Append(pn.UserText);
            if (o is GH_BooleanToggle t) sb.Append('=').Append(t.Value);
            if (o is GH_Group g) sb.Append('#').Append(string.Join(",", g.ObjectIDs));
            // position after a separator: a pure move is reported as a count, not as shorthand lines
            var piv = o.Attributes?.Pivot ?? System.Drawing.PointF.Empty;
            sb.Append(MoveSep).Append((int)piv.X).Append(',').Append((int)piv.Y);
            return sb.ToString();
        }

        const char MoveSep = '\u0001';
        static string Semantic(string sig) { int i = sig.LastIndexOf(MoveSep); return i < 0 ? sig : sig.Substring(0, i); }

        public static bool Changed(Dictionary<Guid, string> before)
        {
            var now = Snapshot();
            if (now.Count != before.Count) return true;
            foreach (var kv in now) if (!before.TryGetValue(kv.Key, out var s) || s != kv.Value) return true;
            return false;
        }

        public static void ReportDelta(Dictionary<Guid, string> before, Reply r)
        {
            var d = Doc(false);
            if (d == null) return;
            var now = Snapshot();
            var added = now.Keys.Where(k => k != Guid.Empty && !before.ContainsKey(k)).ToList();
            var removed = before.Keys.Where(k => k != Guid.Empty && !now.ContainsKey(k)).ToList();
            var changedAny = now.Keys.Where(k => k != Guid.Empty && before.TryGetValue(k, out var s) && s != now[k]).ToList();
            var changed = changedAny.Where(k => Semantic(before[k]) != Semantic(now[k])).ToList();
            int moved = changedAny.Count - changed.Count;
            if (added.Count + removed.Count + changedAny.Count == 0) { r.Line("gh: no changes"); return; }
            r.Line($"gh delta +{added.Count} ~{changed.Count} -{removed.Count}" + (moved > 0 ? $", {moved} moved" : "") + (added.Count + changed.Count + removed.Count > 0 ? ":" : ""));
            var objs = added.Concat(changed).Select(g => d.FindObject(g, false)).Where(o => o != null).ToList();
            if (objs.Count > 0) r.Line(Shorthand.Writer.Write(d, objs, new Shorthand.WriteOptions { Messages = true, Compact = true, MaxObjects = 40 }));
            if (removed.Count > 0) r.Line("- " + string.Join(" ", removed.Select(g => Handles.GhPeek(d.DocumentID, g) ?? g.ToString().Substring(0, 8))));
        }

        // ---------- messages ----------

        public static List<(IGH_DocumentObject o, string level, string msg)> Messages(GH_Document d, IEnumerable<IGH_DocumentObject> objs = null)
        {
            var res = new List<(IGH_DocumentObject, string, string)>();
            foreach (var o in objs ?? d.Objects)
            {
                if (!(o is IGH_ActiveObject ao)) continue;
                foreach (var m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Error)) res.Add((o, "error", m));
                foreach (var m in ao.RuntimeMessages(GH_RuntimeMessageLevel.Warning)) res.Add((o, "warn", m));
            }
            return res;
        }
    }
}
