using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace Nextra.Engine.Rh
{
    /// Oriented box + shape class: what an object IS without sending its vertices.
    public class Obb
    {
        public Box Box;
        public double L, W, H;          // sorted extents, L >= W >= H
        public Vector3d LongAxis, ShortAxis;
        public Point3d Center;
        public string Shape;            // point | linear | planar | block

        public string Text()
        {
            var s = Fmt.Dims(L, W, H);
            string dir = "";
            if (Shape == "linear") dir = " " + AxisWord(LongAxis);
            else if (Shape == "planar") dir = " n" + Fmt.V(ShortAxis);
            return $"{s} {Shape}{dir} @{Fmt.P(Center)}";
        }

        static string AxisWord(Vector3d v)
        {
            v.Unitize();
            double z = Math.Abs(v.Z);
            if (z > 0.985) return "vertical";
            if (z < 0.02) return "horiz" + Fmt.V(new Vector3d(Math.Abs(v.X) > 1e-9 && v.X < 0 ? -v.X : v.X, v.X < 0 ? -v.Y : v.Y, 0));
            return "inclined" + Fmt.V(v.Z < 0 ? -v : v);
        }
    }

    public static class Geo
    {
        /// Minimum-volume box over a few cheap candidate frames (world, dominant face, PCA).
        /// Same idea as dev/reference/rhino_tools/structural_utils/analyse_oriented_bbox.py but without mass properties.
        public static Obb ComputeObb(GeometryBase g)
        {
            if (g == null) return null;
            var cands = new List<Plane> { Plane.WorldXY };
            var pts = SamplePoints(g, 400);

            if (g is Brep b) AddFacePlanes(b, cands);
            else if (g is Extrusion ex) { var eb = ex.ToBrep(); if (eb != null) AddFacePlanes(eb, cands); }
            else if (g is Curve c)
            {
                if (c.TryGetPlane(out var cp, RhinoMath.ZeroTolerance * 1000))
                {
                    var x = c.PointAtEnd - c.PointAtStart;
                    if (x.Length > 1e-9 && x.IsParallelTo(cp.ZAxis) == 0) cands.Add(new Plane(cp.Origin, x, Vector3d.CrossProduct(cp.ZAxis, x)));
                    cands.Add(cp);
                }
                var d = c.PointAtEnd - c.PointAtStart;
                if (d.Length > 1e-9) cands.Add(FrameFromAxis(c.PointAtStart, d));
            }
            if (pts.Count >= 3)
            {
                var pca = Pca(pts);
                if (pca.IsValid) cands.Add(pca);
            }

            Box best = Box.Unset;
            double bestV = double.MaxValue;
            foreach (var pl in cands)
            {
                if (!pl.IsValid) continue;
                var bb = g.GetBoundingBox(pl, out Box wb);
                if (!bb.IsValid) continue;
                double dx = wb.X.Length, dy = wb.Y.Length, dz = wb.Z.Length;
                double eps = Math.Max(Math.Max(dx, dy), dz) * 1e-3 + 1e-9;
                double v = (dx + eps) * (dy + eps) * (dz + eps);
                if (v < bestV * 0.999) { bestV = v; best = wb; }
            }
            if (!best.IsValid) return null;
            return FromBox(best);
        }

        public static Obb FromBox(Box box)
        {
            var axes = new[]
            {
                (len: box.X.Length, dir: box.Plane.XAxis),
                (len: box.Y.Length, dir: box.Plane.YAxis),
                (len: box.Z.Length, dir: box.Plane.ZAxis),
            }.OrderByDescending(a => a.len).ToArray();
            var o = new Obb
            {
                Box = box,
                L = axes[0].len, W = axes[1].len, H = axes[2].len,
                LongAxis = axes[0].dir, ShortAxis = axes[2].dir,
                Center = box.Center,
            };
            double L = Math.Max(o.L, 1e-12);
            if (o.L < 1e-6) o.Shape = "point";
            else if (o.W < L * 0.25 && o.H < L * 0.25) o.Shape = "linear";
            else if (o.H < o.W * 0.15) o.Shape = "planar";
            else o.Shape = "block";
            return o;
        }

        static Plane FrameFromAxis(Point3d o, Vector3d x)
        {
            x.Unitize();
            var up = Math.Abs(x.Z) > 0.9 ? Vector3d.XAxis : Vector3d.ZAxis;
            var y = Vector3d.CrossProduct(up, x);
            return new Plane(o, x, y);
        }

        static void AddFacePlanes(Brep b, List<Plane> cands)
        {
            // largest planar face, x along its longest edge
            BrepFace bestF = null; double bestA = 0;
            int n = Math.Min(b.Faces.Count, 200);
            for (int i = 0; i < n; i++)
            {
                var f = b.Faces[i];
                if (!f.IsPlanar(1e-6)) continue;
                var a = AreaMassProperties.Compute(f.DuplicateFace(false), true, false, false, false);
                double area = a?.Area ?? 0;
                if (area > bestA) { bestA = area; bestF = f; }
            }
            if (bestF == null) return;
            bestF.TryGetPlane(out var pl, 1e-6);
            Curve longest = null; double ll = 0;
            foreach (var ei in bestF.AdjacentEdges())
            {
                var e = b.Edges[ei];
                if (e.GetLength() > ll && e.IsLinear(1e-6)) { ll = e.GetLength(); longest = e; }
            }
            if (longest != null)
            {
                var x = longest.PointAtEnd - longest.PointAtStart;
                x = x - (x * pl.ZAxis) * pl.ZAxis;
                if (x.Length > 1e-9) cands.Add(new Plane(pl.Origin, x, Vector3d.CrossProduct(pl.ZAxis, x)));
            }
            cands.Add(pl);
        }

        public static List<Point3d> SamplePoints(GeometryBase g, int max)
        {
            var pts = new List<Point3d>();
            switch (g)
            {
                case Point p: pts.Add(p.Location); break;
                case PointCloud pc: pts.AddRange(pc.GetPoints().Take(max)); break;
                case Curve c:
                    if (c.TryGetPolyline(out var pl)) pts.AddRange(pl);
                    else
                    {
                        int k = Math.Min(max, 64);
                        var ts = c.DivideByCount(k, true);
                        if (ts != null) pts.AddRange(ts.Select(t => c.PointAt(t)));
                    }
                    break;
                case Brep b: pts.AddRange(b.Vertices.Select(v => v.Location).Take(max)); if (pts.Count < 8) pts.AddRange(b.GetBoundingBox(true).GetCorners()); break;
                case Extrusion e: { var eb = e.ToBrep(); if (eb != null) pts.AddRange(eb.Vertices.Select(v => v.Location).Take(max)); break; }
                case Mesh m:
                    {
                        int step = Math.Max(1, m.Vertices.Count / max);
                        for (int i = 0; i < m.Vertices.Count; i += step) pts.Add(m.Vertices[i]);
                        break;
                    }
                case SubD s: { var sm = Mesh.CreateFromSubD(s, 0); if (sm != null) return SamplePoints(sm, max); break; }
                default: { var bb = g.GetBoundingBox(true); if (bb.IsValid) pts.AddRange(bb.GetCorners()); break; }
            }
            return pts;
        }

        /// Principal axes of a point set (Jacobi eigen-decomposition of the covariance).
        public static Plane Pca(List<Point3d> pts)
        {
            var c = new Point3d(pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));
            double[,] m = new double[3, 3];
            foreach (var p in pts)
            {
                var d = new[] { p.X - c.X, p.Y - c.Y, p.Z - c.Z };
                for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) m[i, j] += d[i] * d[j];
            }
            var (vals, vecs) = Jacobi(m);
            int[] order = Enumerable.Range(0, 3).OrderByDescending(i => vals[i]).ToArray();
            var x = new Vector3d(vecs[0, order[0]], vecs[1, order[0]], vecs[2, order[0]]);
            var y = new Vector3d(vecs[0, order[1]], vecs[1, order[1]], vecs[2, order[1]]);
            if (x.Length < 1e-12 || y.Length < 1e-12) return Plane.Unset;
            return new Plane(c, x, y);
        }

        static (double[] vals, double[,] vecs) Jacobi(double[,] a)
        {
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            a = (double[,])a.Clone();
            for (int sweep = 0; sweep < 50; sweep++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                if (off < 1e-14) break;
                for (int p = 0; p < 2; p++)
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-18) continue;
                        double th = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]);
                        double cs = Math.Cos(th), sn = Math.Sin(th);
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = cs * akp - sn * akq; a[k, q] = sn * akp + cs * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = cs * apk - sn * aqk; a[q, k] = sn * apk + cs * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = cs * vkp - sn * vkq; v[k, q] = sn * vkp + cs * vkq;
                        }
                    }
            }
            return (new[] { a[0, 0], a[1, 1], a[2, 2] }, v);
        }

        // ---------- one-line descriptions ----------

        public static string TypeWord(GeometryBase g)
        {
            switch (g)
            {
                case Point _: return "point";
                case PointCloud _: return "pointcloud";
                case LineCurve _: return "line";
                case ArcCurve a: return a.IsCompleteCircle ? "circle" : "arc";
                case PolylineCurve _: return "polyline";
                case PolyCurve _: return "polycurve";
                case NurbsCurve n: return n.IsPolyline() ? "polyline" : n.IsArc() ? "arc" : "curve";
                case Curve _: return "curve";
                case Extrusion _: return "extrusion";
                case Brep b: return b.Faces.Count == 1 ? "surface" : "polysurface";
                case Surface _: return "surface";
                case Mesh _: return "mesh";
                case SubD _: return "subd";
                case InstanceReferenceGeometry _: return "block";
                case TextEntity _: return "text";
                case TextDot _: return "dot";
                case Hatch _: return "hatch";
                case AnnotationBase _: return "annotation";
                default: return g?.ObjectType.ToString().ToLowerInvariant() ?? "null";
            }
        }

        /// e.g. `r3 polysurface solid [Columns] "C1" 3.2x0.4x0.4 linear vertical @(0,0,1.6)`
        public static string Describe(RhinoObject o, string handle = null)
        {
            var g = o.Geometry;
            var sb = new StringBuilder();
            sb.Append(handle ?? Handles.Rh(o.Id)).Append(' ').Append(TypeWord(g));
            var doc = o.Document;
            switch (g)
            {
                case Point p:
                    sb.Append(Layer(o)).Append(' ').Append(Fmt.P(p.Location));
                    return Finish(sb, o);
                case LineCurve lc:
                    sb.Append(Layer(o)).Append($" len {Fmt.N(lc.GetLength())} {Fmt.P(lc.PointAtStart)}->{Fmt.P(lc.PointAtEnd)}");
                    return Finish(sb, o);
                case Curve c when c.TryGetCircle(out var cir, doc.ModelAbsoluteTolerance):
                    sb.Append(Layer(o)).Append($" r={Fmt.N(cir.Radius)} @{Fmt.P(cir.Center)} n{Fmt.V(cir.Normal)}");
                    return Finish(sb, o);
                case TextEntity t:
                    sb.Append(Layer(o)).Append($" \"{Fmt.Trunc(t.PlainText, 40)}\" @{Fmt.P(t.Plane.Origin)}");
                    return Finish(sb, o);
                case TextDot d:
                    sb.Append(Layer(o)).Append($" \"{Fmt.Trunc(d.Text, 30)}\" @{Fmt.P(d.Point)}");
                    return Finish(sb, o);
                case InstanceReferenceGeometry ir:
                    {
                        var def = doc.InstanceDefinitions.FindId(ir.ParentIdefId);
                        var origin = Point3d.Origin; origin.Transform(ir.Xform);
                        sb.Append($" \"{def?.Name}\"").Append(Layer(o)).Append($" @{Fmt.P(origin)}");
                        var bb = o.Geometry.GetBoundingBox(true);
                        if (bb.IsValid) sb.Append($" bbox {Fmt.Dims(bb.Diagonal.X, bb.Diagonal.Y, bb.Diagonal.Z)}");
                        return Finish(sb, o);
                    }
            }

            if (g is Curve cu)
            {
                sb.Append(cu.IsClosed ? " closed" : "");
                if (cu.TryGetPolyline(out var pl)) sb.Append($" {pl.Count - (cu.IsClosed ? 1 : 0)}v");
                else if (!(cu is ArcCurve)) sb.Append($" deg{cu.Degree}");
                sb.Append(Layer(o)).Append(" len ").Append(Fmt.N(cu.GetLength()));
            }
            else if (g is Brep br) sb.Append(br.IsSolid ? " solid" : $" open {br.Faces.Count}f").Append(Layer(o));
            else if (g is Extrusion ex) sb.Append(ex.IsSolid ? " solid" : " open").Append(Layer(o));
            else if (g is Mesh m) sb.Append($" {m.Faces.Count}f").Append(m.IsClosed ? " closed" : "").Append(Layer(o));
            else sb.Append(Layer(o));

            var obb = ComputeObb(g);
            if (obb != null) sb.Append(' ').Append(obb.Text());
            return Finish(sb, o);
        }

        static string Layer(RhinoObject o)
        {
            var l = o.Document.Layers[o.Attributes.LayerIndex];
            var s = " [" + (l?.FullPath ?? "?") + "]";
            if (!string.IsNullOrEmpty(o.Attributes.Name)) s += $" \"{o.Attributes.Name}\"";
            return s;
        }

        static string Finish(StringBuilder sb, RhinoObject o)
        {
            if (o.IsHidden) sb.Append(" (hidden)");
            if (o.IsLocked) sb.Append(" (locked)");
            if (!o.Visible && !o.IsHidden) sb.Append(" (layer off)");
            return sb.ToString();
        }

        /// Level-2 detail: topology summary, grouped faces by direction, user text.
        public static string Detail(RhinoObject o)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Describe(o));
            var g = o.Geometry;
            var tol = o.Document.ModelAbsoluteTolerance;
            if (g is Extrusion ex0) g = ex0.ToBrep();
            switch (g)
            {
                case Brep b:
                    {
                        sb.AppendLine($"  topo: {b.Faces.Count} faces, {b.Edges.Count} edges, {b.Vertices.Count} verts, naked edges {b.Edges.Count(e => e.Valence == EdgeAdjacency.Naked)}");
                        var vm = b.IsSolid ? VolumeMassProperties.Compute(b, true, false, false, false) : null;
                        var am = AreaMassProperties.Compute(b, true, false, false, false);
                        sb.AppendLine($"  area {Fmt.N(am?.Area ?? 0)}" + (vm != null ? $", volume {Fmt.N(vm.Volume)}" : ""));
                        // group faces by normal direction: cheap description of a solid's shape
                        var groups = new Dictionary<string, (int n, double a)>();
                        int curved = 0;
                        foreach (var f in b.Faces.Take(500))
                        {
                            if (f.TryGetPlane(out var pl, tol))
                            {
                                var n = pl.ZAxis; if (f.OrientationIsReversed) n.Reverse();
                                var key = Fmt.V(n);
                                var a = AreaMassProperties.Compute(f.DuplicateFace(false), true, false, false, false)?.Area ?? 0;
                                groups[key] = groups.TryGetValue(key, out var x) ? (x.n + 1, x.a + a) : (1, a);
                            }
                            else curved++;
                        }
                        if (groups.Count > 0)
                            sb.AppendLine("  planar faces by normal: " + string.Join(", ", groups.OrderByDescending(kv => kv.Value.a).Take(12).Select(kv => $"{kv.Key}×{kv.Value.n} A={Fmt.N(kv.Value.a)}")));
                        if (curved > 0) sb.AppendLine($"  curved faces: {curved}");
                        break;
                    }
                case Curve c:
                    {
                        sb.AppendLine($"  start {Fmt.P(c.PointAtStart)} end {Fmt.P(c.PointAtEnd)}, closed {c.IsClosed}, planar {c.IsPlanar(tol)}");
                        if (c.TryGetPolyline(out var pl))
                        {
                            sb.Append("  vertices: ");
                            sb.AppendLine(string.Join(" ", pl.Take(24).Select(Fmt.P)) + (pl.Count > 24 ? $" …(+{pl.Count - 24}, use level verts)" : ""));
                        }
                        else if (c is PolyCurve pc) sb.AppendLine($"  segments: {pc.SegmentCount}");
                        break;
                    }
                case Mesh m:
                    sb.AppendLine($"  {m.Vertices.Count} verts, {m.Faces.Count} faces, closed {m.IsClosed}, naked edges {m.GetNakedEdges()?.Length ?? 0}");
                    break;
            }
            var a2 = o.Attributes;
            var keys = a2.GetUserStrings();
            if (keys.Count > 0) sb.AppendLine("  usertext: " + string.Join(", ", keys.AllKeys.Take(20).Select(k => $"{k}={Fmt.Trunc(keys[k], 40)}")));
            if (a2.ColorSource == ObjectColorSource.ColorFromObject) sb.AppendLine($"  color #{a2.ObjectColor.R:X2}{a2.ObjectColor.G:X2}{a2.ObjectColor.B:X2}");
            return sb.ToString().TrimEnd();
        }

        /// Level-3: vertices / control points, capped.
        public static string Verts(RhinoObject o, int max)
        {
            var pts = new List<Point3d>();
            var g = o.Geometry;
            if (g is Extrusion e) g = e.ToBrep();
            switch (g)
            {
                case Brep b: pts.AddRange(b.Vertices.Select(v => v.Location)); break;
                case Curve c:
                    if (c.TryGetPolyline(out var pl)) pts.AddRange(pl);
                    else { var nc = c.ToNurbsCurve(); if (nc != null) pts.AddRange(nc.Points.Select(p => p.Location)); }
                    break;
                case Mesh m: pts.AddRange(m.Vertices.Select(v => (Point3d)v)); break;
                case Point p: pts.Add(p.Location); break;
                default: pts.AddRange(SamplePoints(g, max)); break;
            }
            var sb = new StringBuilder();
            sb.Append($"{Handles.Rh(o.Id)} {pts.Count} pts: ");
            sb.Append(string.Join(" ", pts.Take(max).Select(Fmt.P)));
            if (pts.Count > max) sb.Append($" …(+{pts.Count - max}; raise limit or use run)");
            return sb.ToString();
        }
    }
}
