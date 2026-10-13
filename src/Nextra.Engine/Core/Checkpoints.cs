using System;
using System.Collections.Generic;
using System.Linq;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;
using Rhino;

namespace Nextra.Engine
{
    /// Every mutating call (run, gh_edit) gets a checkpoint id (c1, c2, …).
    /// Rhino part: exact object-level revert from the call's delta. GH part: serialized document archive
    /// taken before the change (independent of GH's own undo stack).
    public static class Checkpoints
    {
        const int Keep = 30;

        static List<Dictionary<string, object>> List
        {
            get
            {
                if (!Entry.State.TryGetValue("ckpt.list", out var o) || !(o is List<Dictionary<string, object>> l))
                {
                    l = new List<Dictionary<string, object>>();
                    Entry.State["ckpt.list"] = l;
                    Entry.State["ckpt.next"] = new int[] { 1 };
                }
                return l;
            }
        }

        /// Create a checkpoint before a change. Call Complete() after with the Rhino delta.
        public static Dictionary<string, object> Begin(string label, bool withGh)
        {
            var l = List;
            var next = (int[])Entry.State["ckpt.next"];
            var c = new Dictionary<string, object> { ["id"] = "c" + next[0]++, ["label"] = label, ["time"] = DateTime.Now };
            if (withGh)
            {
                var gh = GhCore.Doc(false);
                if (gh != null)
                {
                    c["gh"] = GhCore.Archive(gh);
                    c["ghdoc"] = gh.DocumentID;
                }
            }
            l.Add(c);
            while (l.Count > Keep) l.RemoveAt(0);
            return c;
        }

        public static void Complete(Dictionary<string, object> c, DeltaTracker d)
        {
            if (d == null) return;
            c["rh_doc"] = d.DocumentSerial;
            c["rh_added"] = d.Added.ToList();
            c["rh_before"] = new Dictionary<Guid, object[]>(d.Before);
            if (d.LayersChanged)
            {
                c["rh_layers"] = d.LayersBefore;
                c["rh_current_layer"] = d.CurrentLayerBefore;
            }
        }

        /// Drop a checkpoint that turned out to change nothing.
        public static void Discard(Dictionary<string, object> c) => List.Remove(c);

        public static string Describe()
        {
            var l = List;
            if (l.Count == 0) return "no checkpoints";
            return string.Join("\n", l.AsEnumerable().Reverse().Take(15).Select(c =>
                $"{c["id"]} {((DateTime)c["time"]):HH:mm:ss} {c["label"]}" +
                (c.ContainsKey("rh_added") ? $" rhino(+{((List<Guid>)c["rh_added"]).Count} ~/-{((Dictionary<Guid, object[]>)c["rh_before"]).Count})" : "") +
                (c.ContainsKey("gh") ? " gh" : "")));
        }

        /// Revert everything from checkpoint `id` (inclusive) to now, newest first.
        public static string RevertTo(string id, int steps)
        {
            var l = List;
            int idx;
            if (!string.IsNullOrEmpty(id))
            {
                idx = l.FindIndex(c => (string)c["id"] == id);
                if (idx < 0) throw new NxError($"checkpoint '{id}' not found", "undo {list:true} shows available checkpoints (last 30 kept)");
            }
            else idx = Math.Max(0, l.Count - Math.Max(1, steps));
            if (l.Count == 0) throw new NxError("nothing to undo");

            var doc = RhinoDoc.ActiveDoc;
            var lines = new List<string>();
            var ghRestore = new Dictionary<Guid, (byte[] data, string from)>();   // oldest archive per GH document
            for (int i = l.Count - 1; i >= idx; i--)
            {
                var c = l[i];
                if (c.ContainsKey("rh_added") && doc != null && Equals(c["rh_doc"], doc.RuntimeSerialNumber))
                {
                    var layers = c.TryGetValue("rh_layers", out var ls) ? (Dictionary<Guid, object[]>)ls : null;
                    if (layers != null) DeltaTracker.RestoreLayers(doc, layers, (int)c["rh_current_layer"]);
                    lines.Add($"{c["id"]}: " + DeltaTracker.Revert(doc, (List<Guid>)c["rh_added"], (Dictionary<Guid, object[]>)c["rh_before"]));
                    if (layers != null) { DeltaTracker.RemoveAddedLayers(doc, layers); lines.Add("layers restored"); }
                }
                if (c.ContainsKey("gh")) ghRestore[(Guid)c["ghdoc"]] = ((byte[])c["gh"], (string)c["id"]);   // iterating newest→oldest: oldest wins
            }
            foreach (var kv in ghRestore) lines.Add($"{kv.Value.from}: " + GhCore.Restore(kv.Value.data, kv.Key));
            int removed = l.Count - idx;
            l.RemoveRange(idx, removed);
            if (lines.Count == 0) lines.Add("nothing changed in those checkpoints");
            return string.Join("\n", lines);
        }
    }

    public static class UndoTool
    {
        public static void Run(Args a, Reply r)
        {
            if (a.Bool("list", false)) { r.Line(Checkpoints.Describe()); return; }
            var id = a.Str("checkpoint", a.Str("checkpoint_id", null));
            int steps = a.Int("steps", 1);
            var done = Checkpoints.RevertTo(id, steps);   // throws (nothing reverted) before any output
            r.Line("reverted:");
            r.Line(done);
        }
    }
}
