using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Nextra.Engine.Rh
{
    /// Records Rhino doc changes during one call: what was added / modified / deleted, plus copies of the
    /// "before" state so the call can be reverted exactly (independent of the user's undo stack).
    public class DeltaTracker : IDisposable
    {
        static readonly List<DeltaTracker> Active = new List<DeltaTracker>();

        public readonly HashSet<Guid> Added = new HashSet<Guid>();
        public readonly HashSet<Guid> Modified = new HashSet<Guid>();
        public readonly HashSet<Guid> Deleted = new HashSet<Guid>();
        public readonly Dictionary<Guid, object[]> Before = new Dictionary<Guid, object[]>(); // id → {geometry, attributes, RhinoObject}
        readonly RhinoDoc _doc;
        public uint DocumentSerial => _doc.RuntimeSerialNumber;
        public readonly Dictionary<Guid, object[]> LayersBefore;
        public readonly int CurrentLayerBefore;

        public DeltaTracker(RhinoDoc doc)
        {
            _doc = doc;
            CurrentLayerBefore = doc.Layers.CurrentLayerIndex;
            LayersBefore = doc.Layers.Where(l => !l.IsDeleted).ToDictionary(l => l.Id,
                l => new object[] { l.ToJSON(new Rhino.FileIO.SerializationOptions()), l.Index, l.DataCRC(0) });
            RhinoDoc.AddRhinoObject += OnAdd;
            RhinoDoc.UndeleteRhinoObject += OnAdd;
            RhinoDoc.DeleteRhinoObject += OnDelete;
            RhinoDoc.ReplaceRhinoObject += OnReplace;
            RhinoDoc.ModifyObjectAttributes += OnAttr;
            Active.Add(this);
        }

        public static void DetachAll()
        {
            foreach (var t in Active.ToList()) t.Dispose();
        }

        public void Dispose()
        {
            RhinoDoc.AddRhinoObject -= OnAdd;
            RhinoDoc.UndeleteRhinoObject -= OnAdd;
            RhinoDoc.DeleteRhinoObject -= OnDelete;
            RhinoDoc.ReplaceRhinoObject -= OnReplace;
            RhinoDoc.ModifyObjectAttributes -= OnAttr;
            Active.Remove(this);
        }

        void Remember(RhinoObject o)
        {
            if (o == null || Added.Contains(o.Id) || Before.ContainsKey(o.Id)) return;
            Before[o.Id] = new object[] { o.Geometry?.Duplicate(), o.Attributes?.Duplicate(), o };
        }

        void OnAdd(object s, RhinoObjectEventArgs e)
        {
            if (e.TheObject?.Document != _doc) return;
            var id = e.ObjectId;
            if (Deleted.Remove(id)) { Modified.Add(id); return; }  // replace = delete + add
            if (!Before.ContainsKey(id)) Added.Add(id);
        }

        void OnDelete(object s, RhinoObjectEventArgs e)
        {
            if (e.TheObject?.Document != _doc) return;
            var id = e.ObjectId;
            if (Added.Remove(id)) return;                          // created and deleted in the same call
            Remember(e.TheObject);
            Modified.Remove(id);
            Deleted.Add(id);
        }

        void OnReplace(object s, RhinoReplaceObjectEventArgs e)
        {
            if (e.Document != _doc) return;
            if (Added.Contains(e.ObjectId)) return;
            Remember(e.OldRhinoObject);
            Modified.Add(e.ObjectId);
        }

        void OnAttr(object s, RhinoModifyObjectAttributesEventArgs e)
        {
            if (e.Document != _doc || Added.Contains(e.RhinoObject.Id)) return;
            if (!Before.ContainsKey(e.RhinoObject.Id))
                Before[e.RhinoObject.Id] = new object[] { e.RhinoObject.Geometry?.Duplicate(), e.OldAttributes?.Duplicate(), e.RhinoObject };
            Modified.Add(e.RhinoObject.Id);
        }

        string Summary(RhinoObject o)
        {
            var l = _doc.Layers[o.Attributes.LayerIndex]?.FullPath ?? "?";
            var obb = Geo.ComputeObb(o.Geometry);
            string dims = obb == null ? "" : " " + Fmt.Dims(R(obb.L), R(obb.W), R(obb.H)) + " " + obb.Shape + (obb.Shape == "linear" ? " " + Obb.AxisClass(obb.LongAxis) : "");
            return (Added.Contains(o.Id) ? "+ " : "~ ") + $"[{l}] {Geo.TypeWord(o.Geometry)}{dims}";
        }

        static double R(double v) { double m = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(Math.Abs(v), 1e-9))) - 2); return Math.Round(v / m) * m; }

        public bool LayersChanged => _doc.Layers.CurrentLayerIndex != CurrentLayerBefore ||
            _doc.Layers.Count(l => !l.IsDeleted) != LayersBefore.Count ||
            _doc.Layers.Where(l => !l.IsDeleted).Any(l => !LayersBefore.TryGetValue(l.Id, out var old) || (uint)old[2] != l.DataCRC(0));

        public bool Empty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0 && !LayersChanged;

        /// Compact delta text: one line per object (handles + description), capped.
        public void Report(Reply r, int max = 40)
        {
            if (Empty) { r.Line("rhino: no changes"); return; }
            if (LayersChanged) r.Line("rhino layers changed (included in checkpoint)");
            if (Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0) return;
            var parts = new List<string>();
            if (Added.Count > 0) parts.Add($"+{Added.Count}");
            if (Modified.Count > 0) parts.Add($"~{Modified.Count}");
            if (Deleted.Count > 0) parts.Add($"-{Deleted.Count}");
            r.Line("rhino delta " + string.Join(" ", parts) + ":");
            var objs = Added.Concat(Modified).Select(id => _doc.Objects.FindId(id)).Where(o => o != null).ToList();
            if (objs.Count <= 12)
            {
                foreach (var o in objs) r.Line((Added.Contains(o.Id) ? "+ " : "~ ") + Geo.Describe(o));
            }
            else
            {
                // many objects: one line per (change, layer, type, shape, size) group with handle ranges
                foreach (var g in objs.GroupBy(o => Summary(o)).OrderBy(g => g.Key))
                {
                    var hs = g.Select(o => Handles.Rh(o.Id)).ToList();
                    var bb = BoundingBox.Empty; foreach (var o in g) bb.Union(o.Geometry.GetBoundingBox(true));
                    r.Line($"{g.Key} ×{g.Count()} {ViewTool.Ranges(hs)} z {Fmt.N(bb.Min.Z)}..{Fmt.N(bb.Max.Z)}");
                }
                r.Line("(details: query filter:'r12 r13' or 'layer:X')");
            }
            if (Deleted.Count > 0) r.Line("- " + ViewTool.Ranges(Deleted.Select(Handles.Rh)));
        }

        /// Undo this delta (used by checkpoints).
        public static string Revert(RhinoDoc doc, IEnumerable<Guid> added, Dictionary<Guid, object[]> before)
        {
            int del = 0, rest = 0, fail = 0;
            foreach (var id in added) if (doc.Objects.Delete(id, true)) del++;
            foreach (var kv in before)
            {
                var geo = kv.Value[0] as GeometryBase;
                var attrs = kv.Value[1] as ObjectAttributes;
                var obj = kv.Value[2] as RhinoObject;
                var cur = doc.Objects.FindId(kv.Key);
                bool ok;
                if (cur != null)
                {
                    ok = geo == null || doc.Objects.Replace(kv.Key, geo, true) || ReplaceAny(doc, kv.Key, geo);
                    if (attrs != null) doc.Objects.ModifyAttributes(kv.Key, attrs, true);
                }
                else
                {
                    ok = obj != null && doc.Objects.Undelete(obj);
                    if (!ok && geo != null)
                    {
                        var a2 = attrs?.Duplicate() ?? new ObjectAttributes();
                        a2.ObjectId = kv.Key;
                        ok = doc.Objects.Add(geo, a2) != Guid.Empty;
                    }
                }
                if (ok) rest++; else fail++;
            }
            doc.Views.Redraw();
            return $"rhino: removed {del} added, restored {rest}" + (fail > 0 ? $", {fail} failed" : "");
        }

        static bool ReplaceAny(RhinoDoc doc, Guid id, GeometryBase g)
        {
            switch (g)
            {
                case Brep b: return doc.Objects.Replace(id, b);
                case Curve c: return doc.Objects.Replace(id, c);
                case Mesh m: return doc.Objects.Replace(id, m);
                case Point p: return doc.Objects.Replace(id, p.Location);
                case Extrusion x: return doc.Objects.Replace(id, x);
                case SubD s: return doc.Objects.Replace(id, s);
                default: return false;
            }
        }

        /// Restore original layers before restoring objects that refer to their indices.
        public static void RestoreLayers(RhinoDoc doc, Dictionary<Guid, object[]> before, int current)
        {
            foreach (var kv in before.OrderBy(kv => (int)kv.Value[1]))
            {
                int index = (int)kv.Value[1];
                using var saved = (Layer)Rhino.Runtime.CommonObject.FromJSON((string)kv.Value[0]);
                if (index >= doc.Layers.Count || doc.Layers[index].Id != kv.Key)
                    throw new NxError("cannot restore layer " + saved.Name + ": layer table was replaced");
                if (doc.Layers[index].IsDeleted && !doc.Layers.Undelete(index))
                    throw new NxError("cannot undelete layer " + saved.Name);
                if (doc.Layers[index].DataCRC(0) != (uint)kv.Value[2] && !doc.Layers.Modify(saved, index, true))
                    throw new NxError("cannot restore layer " + saved.Name);
            }
            if (doc.Layers.CurrentLayerIndex != current && !doc.Layers.SetCurrentLayerIndex(current, true))
                throw new NxError("cannot restore the current layer");
        }

        public static void RemoveAddedLayers(RhinoDoc doc, Dictionary<Guid, object[]> before)
        {
            // Children are removed first, including children added under existing parents.
            var added = doc.Layers.Where(l => !l.IsDeleted && !before.ContainsKey(l.Id))
                .OrderByDescending(l => l.FullPath.Count(c => c == ':')).ThenByDescending(l => l.Index).ToList();
            foreach (var l in added)
                if (!doc.Layers.Delete(l.Index, true)) throw new NxError("cannot remove added layer '" + l.FullPath + "' (it may contain later user objects)");
        }
    }
}
