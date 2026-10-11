using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Nextra.Engine.Gh;
using Nextra.Engine.Rh;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.Geometry;
using Font = System.Drawing.Font;
using Point = Rhino.Geometry.Point;

namespace Nextra.Engine
{
    /// `view`: low-res image + 8×8 grid + handle tags. Region-first: an overview tags nothing when crowded
    /// and reports per-cell counts instead; the LLM then says focus:"C3:E5" and gets that region re-rendered
    /// at higher resolution with tags. The user's viewport is always restored.
    public static class ViewTool
    {
        static readonly Regex CellRx = new Regex(@"^[A-Ha-h][1-8](:[A-Ha-h][1-8])?$");
        public static bool IsCells(string s) => s != null && CellRx.IsMatch(s.Trim());

        public static void Run(Args a, Reply r)
        {
            var target = (a.Str("target", "rhino") ?? "rhino").ToLowerInvariant();
            if (target == "gh" || target == "grasshopper") { GhView.Run(a, r); return; }
            if (target == "gh_preview" || target == "preview") { GhPreviewView.Run(a, r); return; }
            if (target != "rhino") throw new NxError($"target '{target}' unknown", "target: rhino | gh | gh_preview");
            RhinoCapture(a, r, null);
        }

        // ---------- shared output helpers ----------

        public static (int w, int h) Size(Args a)
        {
            var n = a.Nums("size");
            if (n == null || n.Length == 0) return (800, 600);
            int w = (int)Math.Clamp(n[0], 200, 2000);
            int h = n.Length > 1 ? (int)Math.Clamp(n[1], 150, 2000) : (int)(w * 0.75);
            return (w, h);
        }

        public static string SaveImage(Bitmap bmp, string prefix)
        {
            var dir = Path.Combine(Entry.AppDir, "captures", DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dir);
            var name = $"{prefix}_{DateTime.Now:HHmmss_fff}.png";
            var path = Path.Combine(dir, name);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            var copy = EngineConfig.Str("capture_copy_dir", null);
            if (!string.IsNullOrEmpty(copy))
            {
                try { Directory.CreateDirectory(copy); File.Copy(path, Path.Combine(copy, name), true); } catch { }
            }
            return path;
        }

        public static string Ranges(IEnumerable<string> handles)
        {
            // r1 r2 r3 r7 → r1-r3 r7
            var groups = handles.GroupBy(h => new string(h.TakeWhile(char.IsLetter).ToArray()));
            var parts = new List<string>();
            foreach (var g in groups)
            {
                var nums = g.Select(h => int.TryParse(h.Substring(g.Key.Length), out var n) ? n : -1).Where(n => n >= 0).OrderBy(n => n).ToList();
                for (int i = 0; i < nums.Count; i++)
                {
                    int j = i;
                    while (j + 1 < nums.Count && nums[j + 1] == nums[j] + 1) j++;
                    parts.Add(j > i + 1 ? $"{g.Key}{nums[i]}-{g.Key}{nums[j]}" : j == i + 1 ? $"{g.Key}{nums[i]} {g.Key}{nums[j]}" : $"{g.Key}{nums[i]}");
                    i = j;
                }
            }
            return string.Join(" ", parts);
        }

        // ---------- Rhino ----------

        class CaptureConduit : DisplayConduit
        {
            public HashSet<Guid> Show;
            public Transform W2S = Transform.Unset;
            public int VW, VH;
            public List<GeometryBase> Geometry;   // unbaked GH preview geometry drawn only during capture
            readonly DisplayMaterial _mat = new DisplayMaterial(Color.FromArgb(200, 70, 70), 0.35);

            protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
            {
                if (Geometry == null) return;
                foreach (var g in Geometry) e.IncludeBoundingBox(g.GetBoundingBox(false));
            }

            protected override void PostDrawObjects(DrawEventArgs e)
            {
                if (Geometry == null) return;
                var wire = Color.FromArgb(150, 20, 20);
                foreach (var g in Geometry)
                {
                    switch (g)
                    {
                        case Brep b: e.Display.DrawBrepShaded(b, _mat); e.Display.DrawBrepWires(b, wire, 0); break;
                        case Extrusion x: { var b2 = x.ToBrep(); e.Display.DrawBrepShaded(b2, _mat); e.Display.DrawBrepWires(b2, wire, 0); break; }
                        case Mesh m: e.Display.DrawMeshShaded(m, _mat); e.Display.DrawMeshWires(m, wire); break;
                        case Curve c: e.Display.DrawCurve(c, wire, 2); break;
                        case Point pt: e.Display.DrawPoint(pt.Location, PointStyle.X, 4, wire); break;
                        case Surface s: { var b3 = s.ToBrep(); e.Display.DrawBrepShaded(b3, _mat); e.Display.DrawBrepWires(b3, wire, 0); break; }
                    }
                }
            }

            protected override void ObjectCulling(CullObjectEventArgs e)
            {
                if (Show != null && e.RhinoObject != null && !Show.Contains(e.RhinoObject.Id)) e.CullObject = true;
            }

            public int ExpectW, ExpectH, Hits; public string Seen;

            protected override void DrawForeground(DrawEventArgs e)
            {
                var s = e.Viewport.Size;
                Seen = (Seen ?? "") + $"{s.Width}x{s.Height} ";
                // only the offscreen capture pass (screen redraws of the same view also call this)
                if (ExpectW > 0 && (s.Width != ExpectW || s.Height != ExpectH)) return;
                W2S = e.Viewport.GetTransform(CoordinateSystem.World, CoordinateSystem.Screen);
                VW = s.Width; VH = s.Height; Hits++;
            }
        }

        static Dictionary<string, object> Record
        {
            get => Entry.State.TryGetValue("view.rhino", out var o) ? o as Dictionary<string, object> : null;
            set => Entry.State["view.rhino"] = value;
        }

        /// Point test for a cell range of the LAST rhino image (used by query filter "cells:C3:E5").
        public static Func<Point3d, bool> CellTest(string cells, string target)
        {
            if (target == "gh") return null;
            var rec = Record ?? throw new NxError("no previous rhino view", "call view first, then use cells from its grid");
            var xf = (Transform)rec["xform"];
            int bw = (int)rec["bw"], bh = (int)rec["bh"];
            var crop = (RectangleF)rec["crop"];
            var c = GridOverlay.Parse(cells);
            var reg = new RectangleF(crop.X + c.X * crop.Width, crop.Y + c.Y * crop.Height, c.Width * crop.Width, c.Height * crop.Height);
            return p =>
            {
                var s = p; s.Transform(xf);
                return reg.Contains((float)(s.X / bw), (float)(s.Y / bh));
            };
        }

        static RhinoView PickView(RhinoDoc doc, string name, out DefinedViewportProjection? proj, out bool iso)
        {
            proj = null; iso = false;
            var active = doc.Views.ActiveView ?? doc.Views.GetViewList(true, false).FirstOrDefault();
            if (active == null) throw new NxError("no Rhino view available", "open a viewport in Rhino");
            if (string.IsNullOrEmpty(name) || name == "current" || name == "active") return active;
            var n = name.ToLowerInvariant();
            switch (n)
            {
                case "top": proj = DefinedViewportProjection.Top; break;
                case "bottom": proj = DefinedViewportProjection.Bottom; break;
                case "front": proj = DefinedViewportProjection.Front; break;
                case "back": proj = DefinedViewportProjection.Back; break;
                case "left": proj = DefinedViewportProjection.Left; break;
                case "right": proj = DefinedViewportProjection.Right; break;
                case "persp": case "perspective": proj = DefinedViewportProjection.Perspective; break;
                case "iso": case "isometric": iso = true; break;
                default:
                    var v = doc.Views.Find(name, false);
                    if (v != null) return v;
                    var nv = doc.NamedViews.FindByName(name);
                    if (nv >= 0) { doc.NamedViews.Restore(nv, active.ActiveViewport); return active; }
                    throw new NxError($"view '{name}' not found", "view: current | top | front | right | left | back | persp | iso | <viewport name>");
            }
            return active;
        }

        static void SetDisplayMode(RhinoViewport vp, string mode)
        {
            if (string.IsNullOrEmpty(mode)) return;
            var all = DisplayModeDescription.GetDisplayModes();
            var m = all.FirstOrDefault(d => d.EnglishName.Equals(mode, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(d => d.EnglishName.StartsWith(mode, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(d => d.EnglishName.IndexOf(mode, StringComparison.OrdinalIgnoreCase) >= 0);
            if (m == null) throw new NxError($"display mode '{mode}' not found", "modes: " + string.Join(", ", all.Select(d => d.EnglishName)));
            vp.DisplayMode = m;
        }

        /// Shared Rhino capture used by view and by GH preview view (extraTags).
        public static void RhinoCapture(Args a, Reply r, Action<RhinoDoc, CaptureContext> extra)
        {
            var doc = RhinoDoc.ActiveDoc ?? throw new NxError("no active Rhino document");
            var (w, h) = Size(a);
            string focus = a.Str("focus", null)?.Trim();
            string tagMode = (a.Str("tags", "auto") ?? "auto").ToLowerInvariant();
            bool isolate = a.Bool("isolate", false);
            bool grid = a.Bool("grid", true);
            int tagLimit = a.Int("tag_limit", 30);
            var view = PickView(doc, a.Str("view", null), out var proj, out bool iso);
            var vp = view.ActiveViewport;
            var saved = new ViewportInfo(vp);
            var savedMode = vp.DisplayMode;
            var rec = Record;
            var conduit = new CaptureConduit();
            RectangleF crop = new RectangleF(0, 0, 1, 1);
            HashSet<Guid> focusIds = null;
            int scale = 1;
            string how;
            var ctx = new CaptureContext();

            try
            {
                var mode = a.Str("mode", "shaded");
                if (mode != "current") SetDisplayMode(vp, mode);
                extra?.Invoke(doc, ctx);
                if (IsCells(focus))
                {
                    if (rec == null) throw new NxError("no previous rhino view to zoom into", "call view without focus first");
                    vp.SetViewProjection((ViewportInfo)rec["cam"], true);
                    var prev = (RectangleF)rec["crop"];
                    var c = GridOverlay.Parse(focus);
                    crop = new RectangleF(prev.X + c.X * prev.Width, prev.Y + c.Y * prev.Height, c.Width * prev.Width, c.Height * prev.Height);
                    scale = (int)Math.Ceiling(1.0 / Math.Max(crop.Width, crop.Height));
                    var bw0 = (int)rec["bw"]; var bh0 = (int)rec["bh"];
                    w = bw0; h = bh0;
                    if (w * scale > 8000 || h * scale > 8000)
                        throw new NxError("zoom too deep for region crop", "focus on handles (e.g. focus:'r12 r13') or a filter instead of nested cells");
                    how = $"region {focus} of previous view (x{scale})";
                    if (rec.TryGetValue("isolate", out var iso0) && iso0 is HashSet<Guid> hs) { focusIds = hs; conduit.Show = hs; }
                }
                else
                {
                    if (proj.HasValue) vp.SetProjection(proj.Value, null, false);
                    if (iso)
                    {
                        vp.ChangeToParallelProjection(true);
                        vp.SetCameraLocations(Point3d.Origin, new Point3d(1, -1, 1) * 100);
                        vp.SetCameraDirection(new Vector3d(-1, 1, -1), false);
                        vp.CameraUp = Vector3d.ZAxis;
                    }
                    if (ctx.ZoomBox.HasValue) { vp.ZoomBoundingBox(ctx.ZoomBox.Value); how = "zoom to GH preview"; }
                    else if (!string.IsNullOrEmpty(focus) && focus != "all" && focus != "extents" && focus != "keep")
                    {
                        var objs = Filter.Parse(focus).Apply(doc);
                        if (objs.Count == 0) throw new NxError($"focus '{focus}' matched nothing", "use handles from query/view, a filter like 'layer:Walls', or cells like C3:E5");
                        var bb = BoundingBox.Empty;
                        foreach (var o in objs) bb.Union(o.Geometry.GetBoundingBox(true));
                        vp.ZoomBoundingBox(bb);
                        focusIds = new HashSet<Guid>(objs.Select(o => o.Id));
                        how = $"focus {objs.Count} object(s)";
                    }
                    else if (focus == "keep" && rec != null) { vp.SetViewProjection((ViewportInfo)rec["cam"], true); how = "previous camera"; }
                    else if (focus == "keep") how = "user camera";
                    else if (ctx.ZoomBox.HasValue) { vp.ZoomBoundingBox(ctx.ZoomBox.Value); how = "zoom to GH preview"; }
                    else { vp.ZoomExtents(); how = "zoom extents"; }
                    if (isolate && focusIds != null) conduit.Show = focusIds;
                }
                if (isolate && focusIds != null) conduit.Show = focusIds;
                conduit.Geometry = ctx.Geometry;

                var cam = new ViewportInfo(vp);
                // capture at the viewport's own aspect so the frustum is unchanged and the mapping is exact
                var vsz = vp.Size;
                double asp = vsz.Width / (double)Math.Max(1, vsz.Height);
                if (w / (double)h > asp) w = Math.Max(100, (int)Math.Round(h * asp)); else h = Math.Max(100, (int)Math.Round(w / asp));
                var xfBase = Transform.Scale(Plane.WorldXY, w / (double)vsz.Width, h / (double)vsz.Height, 1) * vp.GetTransform(CoordinateSystem.World, CoordinateSystem.Screen);

                conduit.Enabled = true;
                Bitmap full;
                try
                {
                    bool showGrid = a.Bool("rhino_grid", false);
                    bool g0 = vp.ConstructionGridVisible, a0 = vp.ConstructionAxesVisible, wa0 = vp.WorldAxesVisible;
                    vp.ConstructionGridVisible = showGrid; vp.ConstructionAxesVisible = false; vp.WorldAxesVisible = false;
                    try { full = view.CaptureToBitmap(new System.Drawing.Size(w * scale, h * scale), vp.DisplayMode); }
                    finally { vp.ConstructionGridVisible = g0; vp.ConstructionAxesVisible = a0; vp.WorldAxesVisible = wa0; }
                }
                finally { conduit.Enabled = false; }
                if (full == null) throw new NxError("capture returned nothing", "Rhino could not render offscreen; try a smaller size or another display mode");
                if (a.Bool("debug", false)) r.Note($"vp {vsz.Width}x{vsz.Height}, mode {vp.DisplayMode.EnglishName}, capture {full.Width}x{full.Height}");

                // crop region (for cell zoom) out of the supersampled capture
                Bitmap img;
                if (scale > 1 || crop.Width < 0.999f)
                {
                    var src = new RectangleF(crop.X * full.Width, crop.Y * full.Height, crop.Width * full.Width, crop.Height * full.Height);
                    float s = Math.Min(w / src.Width, h / src.Height);
                    int ow = (int)Math.Round(src.Width * s), oh = (int)Math.Round(src.Height * s);
                    img = new Bitmap(ow, oh);
                    using (var g0 = Graphics.FromImage(img))
                    {
                        g0.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        g0.DrawImage(full, new Rectangle(0, 0, ow, oh), src, GraphicsUnit.Pixel);
                    }
                    full.Dispose();
                }
                else img = full;

                // world → current image pixel
                int iw = img.Width, ih = img.Height;
                Func<Point3d, PointF?> toImg = p =>
                {
                    var d = p - cam.CameraLocation;
                    if (cam.IsPerspectiveProjection && d * cam.CameraDirection <= 0) return null;
                    var s = p; s.Transform(xfBase);
                    double nx = s.X / w, ny = s.Y / h;
                    return new PointF((float)((nx - crop.X) / crop.Width * iw), (float)((ny - crop.Y) / crop.Height * ih));
                };

                // objects in frame
                var es = new ObjectEnumeratorSettings { HiddenObjects = false, LockedObjects = true, NormalObjects = true, IncludeLights = false, IncludeGrips = false, VisibleFilter = true };
                var inFrame = new List<(RhinoObject o, PointF p)>();
                var cellCount = new Dictionary<string, int>();
                var frame = new RectangleF(0, 0, iw, ih);
                foreach (var o in doc.Objects.GetObjectList(es))
                {
                    if (conduit.Show != null && !conduit.Show.Contains(o.Id)) continue;
                    var bb = o.Geometry.GetBoundingBox(true);
                    if (!bb.IsValid) continue;
                    var cp = toImg(bb.Center);
                    if (cp == null || !frame.Contains(cp.Value)) continue;
                    inFrame.Add((o, cp.Value));
                    var cell = GridOverlay.CellOf(cp.Value.X / iw, cp.Value.Y / ih);
                    cellCount[cell] = cellCount.TryGetValue(cell, out var k) ? k + 1 : 1;
                }

                var toTag = inFrame;
                if (focusIds != null && conduit.Show == null) toTag = inFrame.Where(x => focusIds.Contains(x.o.Id)).ToList();
                bool doTags = tagMode == "on" || tagMode == "true" || tagMode == "all" || (tagMode == "auto" && toTag.Count <= tagLimit);
                if (tagMode == "off" || tagMode == "false" || tagMode == "none") doTags = false;

                List<string> placed = new List<string>(), unplaced = new List<string>(), behind = new List<string>();
                using (var g = Graphics.FromImage(img))
                {
                    if (grid) GridOverlay.Draw(g, iw, ih);
                    using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    var placer = new LabelPlacer(g, new RectangleF(1, 1, iw - 2, ih - 2), font);
                    var items = new List<LabelItem>();
                    var dm = vp.DisplayMode.EnglishName.ToLowerInvariant();
                    Func<Point3d, Guid, bool> hidden = (dm.Contains("wire") || dm.Contains("ghost") || dm.Contains("x-ray"))
                        ? (Func<Point3d, Guid, bool>)((p, id) => false) : Occlusion.Build(inFrame.Select(x => x.o), cam);
                    if (doTags && !ctx.SkipRhinoTags) foreach (var (o, _) in toTag.Take(Math.Max(tagLimit, toTag.Count))) items.Add(RhinoLabel(o, toImg, hidden));
                    if (ctx.Tags != null) foreach (var t in ctx.Tags) { var p = toImg(t.pt); if (p != null) items.Add(PointLabel(t.text, p.Value, t.color)); }
                    placer.PlaceAndDraw(g, items.Where(i => i != null && i.Candidates.Count > 0));
                    placed = placer.Placed; unplaced = placer.Unplaced; behind = placer.Behind;
                }
                var path = SaveImage(img, "rhino");
                img.Dispose();
                r.Images.Add(path);

                Record = new Dictionary<string, object>
                {
                    ["xform"] = xfBase, ["bw"] = w, ["bh"] = h, ["crop"] = crop, ["cam"] = cam, ["view"] = view.ActiveViewport.Name,
                    ["isolate"] = conduit.Show,
                };

                r.Line($"rhino view '{view.ActiveViewport.Name}' {iw}x{ih}, {how}{(conduit.Show != null ? ", isolated" : "")}. Grid A-H x 1-8.");
                if (ctx.SkipRhinoTags) { doTags = true; placed = new List<string>(); }   // GH preview: Rhino object stats are noise
                else r.Line($"{inFrame.Count} object(s) in frame" + (doTags ? $", tagged: {Ranges(placed.Where(Handles.IsRhHandle))}" : "."));
                if (behind.Count > 0) r.Line($"behind other geometry (faded, '*'): {Ranges(behind.Where(Handles.IsRhHandle))}");
                if (unplaced.Count > 0) r.Line($"no room for tags: {Ranges(unplaced.Where(Handles.IsRhHandle))} (zoom with focus:cells)");
                if (!doTags && inFrame.Count > 0)
                {
                    r.Line("cells (count): " + string.Join(" ", cellCount.OrderBy(kv => kv.Key[1]).ThenBy(kv => kv.Key[0]).Select(kv => $"{kv.Key}:{kv.Value}")));
                    r.Line($"Too many to tag ({toTag.Count} > {tagLimit}). Next: view focus:'C3:E5' (region), or focus:'layer:X', or query filter:'cells:C3:E5'.");
                }
                if (ctx.Text != null) r.Line(ctx.Text);
            }
            finally
            {
                try
                {
                    vp.SetViewProjection(saved, true);
                    if (vp.DisplayMode.Id != savedMode.Id) vp.DisplayMode = savedMode;
                    view.Redraw();
                }
                catch (Exception e) { r.Note("viewport restore failed: " + e.Message); }
            }
        }

        public class CaptureContext
        {
            public List<(Point3d pt, string text, Color color)> Tags;
            public string Text;
            public BoundingBox? ZoomBox;
            public bool SkipRhinoTags;
            public List<GeometryBase> Geometry;
        }

        static Color ColorFor(GeometryBase g) => g switch
        {
            Curve _ => Color.FromArgb(20, 70, 200),
            Brep _ or Extrusion _ or Surface _ => Color.FromArgb(185, 30, 30),
            Mesh _ or SubD _ => Color.FromArgb(130, 40, 160),
            Point _ or PointCloud _ => Color.FromArgb(20, 130, 50),
            _ => Color.FromArgb(40, 40, 40),
        };

        static LabelItem PointLabel(string text, PointF p, Color c)
        {
            var it = new LabelItem { Text = text, Color = c, Anchor = p, Priority = 0 };
            it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.X + 4 + text.Length * 3.2f, p.Y - 7), Angle = 0 });
            it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.X - 4 - text.Length * 3.2f, p.Y - 7), Angle = 0 });
            it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.X + 4 + text.Length * 3.2f, p.Y + 7), Angle = 0 });
            return it;
        }

        /// Candidate spots that follow the element: along curves / the long axis of linear members,
        /// centred on planar/blocky ones; text sits beside the element, not on a dot.
        static LabelItem RhinoLabel(RhinoObject o, Func<Point3d, PointF?> toImg, Func<Point3d, Guid, bool> hidden)
        {
            var h = Handles.Rh(o.Id);
            var g = o.Geometry;
            var color = ColorFor(g);
            Curve axis = null;
            if (g is Curve c) axis = c;
            Obb obb = null;
            if (axis == null && !(g is Point))
            {
                obb = Geo.ComputeObb(g);
                if (obb != null && obb.Shape == "linear")
                    axis = new LineCurve(obb.Center - obb.LongAxis * obb.L / 2, obb.Center + obb.LongAxis * obb.L / 2);
            }
            if (g is Point pt)
            {
                var p = toImg(pt.Location);
                if (p == null) return null;
                var pl = PointLabel(h, p.Value, color);
                if (hidden(pt.Location, o.Id)) foreach (var c0 in pl.Candidates) c0.Hidden = true;
                return pl;
            }

            var it = new LabelItem { Text = h, Color = color };
            float textHalf = 7.5f;
            if (axis != null)
            {
                var dom = axis.Domain;
                float screenLen = 0;
                var a0 = toImg(axis.PointAtStart); var a1 = toImg(axis.PointAtEnd);
                if (a0 != null && a1 != null) screenLen = Dist(a0.Value, a1.Value);
                it.Priority = screenLen;
                foreach (var f in new[] { 0.5, 0.35, 0.65, 0.2, 0.8 })
                {
                    double t = dom.ParameterAt(f);
                    var p3 = axis.PointAt(t);
                    var tan = axis.TangentAt(t);
                    var p = toImg(p3);
                    var q = toImg(p3 + tan * Math.Max(axis.GetLength() * 0.01, 1e-6));
                    if (p == null || q == null) continue;
                    float dx = q.Value.X - p.Value.X, dy = q.Value.Y - p.Value.Y;
                    float len = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-6) { dx = 1; dy = 0; len = 1; }
                    dx /= len; dy /= len;
                    float ang = LabelPlacer.NormAngle((float)(Math.Atan2(dy, dx) * 180 / Math.PI));
                    float nx = -dy, ny = dx;
                    float off = textHalf + 2;
                    bool hid = hidden(p3, o.Id);
                    it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.Value.X + nx * off, p.Value.Y + ny * off), Angle = ang, Hidden = hid });
                    it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.Value.X - nx * off, p.Value.Y - ny * off), Angle = ang, Hidden = hid });
                    if (it.Anchor == PointF.Empty) it.Anchor = p.Value;
                }
                return it;
            }

            obb ??= Geo.ComputeObb(g);
            var center = obb?.Center ?? g.GetBoundingBox(true).Center;
            // a block/planar object counts as visible if any box corner/centre is unobstructed
            bool blockHidden = obb != null && new[] { center }.Concat(obb.Box.GetCorners()).All(pt => hidden(pt, o.Id));
            var cp = toImg(center);
            if (cp == null) return null;
            it.Anchor = cp.Value;
            float ang2 = 0;
            if (obb != null)
            {
                var e = toImg(center + obb.LongAxis * obb.L / 2);
                if (e != null)
                {
                    float dx = e.Value.X - cp.Value.X, dy = e.Value.Y - cp.Value.Y;
                    it.Priority = (float)Math.Sqrt(dx * dx + dy * dy) * 2;
                    if (Math.Abs(dx) + Math.Abs(dy) > 8) ang2 = LabelPlacer.NormAngle((float)(Math.Atan2(dy, dx) * 180 / Math.PI));
                    if (Math.Abs(ang2) > 60) ang2 = 0; // keep blocky labels mostly readable
                    foreach (var f in new[] { 0f, 0.3f, -0.3f })
                        it.Candidates.Add(new LabelCandidate { Pos = new PointF(cp.Value.X + dx * f, cp.Value.Y + dy * f), Angle = ang2, Hidden = blockHidden });
                }
            }
            it.Candidates.Add(new LabelCandidate { Pos = cp.Value, Angle = 0, Hidden = blockHidden });
            it.Candidates.Add(new LabelCandidate { Pos = new PointF(cp.Value.X, cp.Value.Y - 14), Angle = 0, Hidden = blockHidden });
            it.Candidates.Add(new LabelCandidate { Pos = new PointF(cp.Value.X, cp.Value.Y + 14), Angle = 0, Hidden = blockHidden });
            return it;
        }

        /// Ray-casts from the camera to a point against the other in-frame objects' meshes.
        static class Occlusion
        {
            public static Func<Point3d, Guid, bool> Build(IEnumerable<RhinoObject> objs, ViewportInfo cam)
            {
                var occ = new List<(Guid id, Mesh m, BoundingBox bb)>();
                foreach (var o in objs.Take(400))
                {
                    Mesh m = null;
                    var g = o.Geometry;
                    if (g is Mesh mm) m = mm;
                    else if (g is Brep || g is Extrusion || g is SubD || g is Surface)
                    {
                        var rm = o.GetMeshes(MeshType.Render);
                        if (rm != null && rm.Length > 0) { m = new Mesh(); foreach (var x in rm) m.Append(x); }
                        else
                        {
                            var b = g is Extrusion ex ? ex.ToBrep() : g as Brep ?? (g is Surface sf ? sf.ToBrep() : null);
                            if (b != null) { var ms = Mesh.CreateFromBrep(b, MeshingParameters.FastRenderMesh); if (ms != null) { m = new Mesh(); foreach (var x in ms) m.Append(x); } }
                        }
                    }
                    if (m != null && m.Faces.Count > 0) occ.Add((o.Id, m, m.GetBoundingBox(false)));
                }
                bool persp = cam.IsPerspectiveProjection;
                var all = BoundingBox.Empty; foreach (var x in occ) all.Union(x.bb);
                double tolAbs = Math.Max(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 10, (all.IsValid ? all.Diagonal.Length : 1) * 1e-4);
                var camLoc = cam.CameraLocation; var camDir = cam.CameraDirection; camDir.Unitize();
                return (pt, self) =>
                {
                    Point3d origin; Vector3d dir; double dist;
                    if (persp) { dir = pt - camLoc; dist = dir.Length; dir.Unitize(); origin = camLoc; }
                    else { dist = 1e7; dir = camDir; origin = pt - camDir * dist; }
                    var ray = new Ray3d(origin, dir);
                    var line = new Line(origin, pt);
                    foreach (var (id, m, bb) in occ)
                    {
                        if (id == self) continue;
                        if (!Rhino.Geometry.Intersect.Intersection.LineBox(line, bb, 0, out _)) continue;
                        double t = Rhino.Geometry.Intersect.Intersection.MeshRay(m, ray);
                        if (t >= 0 && t < dist - tolAbs) return true;
                    }
                    return false;
                };
            }
        }

        static float Dist(PointF a, PointF b) => (float)Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    }

    /// Reads %APPDATA%/Nextra/config.json (shared with the host).
    public static class EngineConfig
    {
        public static System.Text.Json.Nodes.JsonObject Load()
        {
            try
            {
                var p = Path.Combine(Entry.AppDir, "config.json");
                if (File.Exists(p)) return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(p)) as System.Text.Json.Nodes.JsonObject;
            }
            catch { }
            return new System.Text.Json.Nodes.JsonObject();
        }

        public static string Str(string k, string d)
        {
            try { var v = Load()[k]; return v == null ? d : v.GetValue<string>(); } catch { return d; }
        }

        public static List<string> List(string k)
        {
            var res = new List<string>();
            try { if (Load()[k] is System.Text.Json.Nodes.JsonArray arr) foreach (var x in arr) res.Add(x.GetValue<string>()); } catch { }
            return res;
        }
    }
}
