using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Nextra.Engine.Rh
{
    /// Resolves a filter to Rhino objects. Filter = one string of tokens, e.g.
    ///   "r3 r7"  ·  "layer:Columns type:curve"  ·  "name:C*"  ·  "sel"  ·  "cells:C3:E5"  ·  "bbox:0,0,0,10,10,5"  ·  "hidden"
    /// Tokens AND together; several handles OR together.
    public class Filter
    {
        public List<Guid> Ids = new List<Guid>();
        public List<string> BadHandles = new List<string>();
        public string Layer, Type, Name, Cells;
        public BoundingBox? Box;
        public bool Selected, IncludeHidden;

        public bool IsEmpty => Ids.Count == 0 && BadHandles.Count == 0 && Layer == null && Type == null && Name == null && Cells == null && Box == null && !Selected;

        public static Filter Parse(string s)
        {
            var f = new Filter();
            if (string.IsNullOrWhiteSpace(s)) return f;
            foreach (var raw in Fmt.FilterTokens(s, "sel", "selected", "hidden", "all", "*"))
            {
                var t = raw.Trim();
                int c = t.IndexOf(':');
                if (c > 0 && !Guid.TryParse(t, out _))
                {
                    var k = t.Substring(0, c).ToLowerInvariant();
                    var v = t.Substring(c + 1);
                    switch (k)
                    {
                        case "layer": f.Layer = v; break;
                        case "type": f.Type = v.ToLowerInvariant(); break;
                        case "name": f.Name = v; break;
                        case "cells": case "cell": f.Cells = v; break;
                        case "bbox":
                            {
                                var n = v.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries);
                                if (n.Length == 1) n = v.Split('|');
                                f.Box = ParseBox(v);
                                break;
                            }
                        default: throw new NxError($"unknown filter key '{k}'", "filter keys: layer: type: name: cells: bbox: plus handles (r3), 'sel', 'hidden'");
                    }
                }
                else if (t == "sel" || t == "selected") f.Selected = true;
                else if (t == "hidden") f.IncludeHidden = true;
                else if (t == "all" || t == "*") { }
                else if (Handles.TryRh(t, out var g)) f.Ids.Add(g);
                else if (Handles.IsRhHandle(t)) f.BadHandles.Add(t);
                else if (c < 0 && f.Name == null) f.Name = t;   // bare word = name pattern
                else throw new NxError($"cannot parse filter token '{t}'");
            }
            return f;
        }

        static BoundingBox ParseBox(string v)
        {
            var n = v.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => double.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if (n.Length != 6) throw new NxError("bbox needs 6 numbers: x0,y0,z0,x1,y1,z1");
            return new BoundingBox(new Point3d(n[0], n[1], n[2]), new Point3d(n[3], n[4], n[5]));
        }

        public static bool TypeMatches(GeometryBase g, string type)
        {
            if (type == null) return true;
            var w = Geo.TypeWord(g);
            switch (type)
            {
                case "curve": case "curves": return g is Curve;
                case "brep": case "breps": case "solid": return g is Brep || g is Extrusion;
                case "surface": return g is Surface || (g is Brep b && b.Faces.Count == 1);
                case "mesh": return g is Mesh;
                case "point": case "points": return g is Point || g is PointCloud;
                case "block": return g is InstanceReferenceGeometry;
                case "text": case "annotation": return g is AnnotationBase || g is TextDot;
                default: return w.StartsWith(type, StringComparison.OrdinalIgnoreCase);
            }
        }

        public List<RhinoObject> Apply(RhinoDoc doc)
        {
            IEnumerable<RhinoObject> src;
            if (Ids.Count > 0) src = Ids.Select(id => doc.Objects.FindId(id)).Where(o => o != null);
            else
            {
                var es = new ObjectEnumeratorSettings
                {
                    HiddenObjects = IncludeHidden,
                    LockedObjects = true,
                    NormalObjects = true,
                    IncludeLights = false,
                    IncludeGrips = false,
                    DeletedObjects = false,
                    ReferenceObjects = true,
                };
                src = doc.Objects.GetObjectList(es);
            }
            var list = new List<RhinoObject>();
            Func<Point3d, bool> inCells = null;
            if (Cells != null) inCells = ViewTool.CellTest(Cells, "rhino");
            foreach (var o in src)
            {
                if (Selected && o.IsSelected(false) == 0) continue;
                if (Layer != null)
                {
                    var l = doc.Layers[o.Attributes.LayerIndex];
                    if (l == null || !(Fmt.Like(l.FullPath, Layer) || Fmt.Like(l.Name, Layer))) continue;
                }
                if (Name != null && !Fmt.Like(o.Attributes.Name, Name)) continue;
                if (!TypeMatches(o.Geometry, Type)) continue;
                if (Box != null || inCells != null)
                {
                    var bb = o.Geometry.GetBoundingBox(true);
                    if (Box != null && !BoundingBox.Intersection(bb, Box.Value).IsValid) continue;
                    if (inCells != null && !inCells(bb.Center)) continue;
                }
                list.Add(o);
            }
            return list;
        }
    }
}
