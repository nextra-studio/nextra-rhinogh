using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using Grasshopper;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Rhino.Geometry;

namespace Nextra.Engine.Gh
{
    /// GH canvas image rendered offscreen by GH's own painter (GenerateHiResImageTile on a private
    /// viewport — the user's canvas view is untouched, the editor may be hidden). Falls back to a
    /// simple self-drawn schematic if that fails. Region-first like the Rhino view.
    public static class GhView
    {
        static Dictionary<string, object> Record
        {
            get => Entry.State.TryGetValue("view.gh", out var o) ? o as Dictionary<string, object> : null;
            set => Entry.State["view.gh"] = value;
        }

        /// Cells of the last GH image → canvas rectangle.
        public static RectangleF CellRect(string cells)
        {
            var rec = Record ?? throw new NxError("no previous gh view", "view {target:'gh'} first");
            var reg = (RectangleF)rec["region"];
            var c = GridOverlay.Parse(cells);
            return new RectangleF(reg.X + c.X * reg.Width, reg.Y + c.Y * reg.Height, c.Width * reg.Width, c.Height * reg.Height);
        }

        public static void Run(Args a, Reply r)
        {
            var d = GhCore.Doc(false);
            if (d == null || d.ObjectCount == 0) { r.Line("GH canvas is empty or not open."); return; }
            GhCore.EnsureEditor();
            var canvas = Instances.ActiveCanvas;
            var (w, h) = ViewTool.Size(a);
            var focus = a.Str("focus", null)?.Trim();
            var tagMode = (a.Str("tags", "auto") ?? "auto").ToLowerInvariant();
            int tagLimit = a.Int("tag_limit", 45);
            bool grid = a.Bool("grid", true);

            RectangleF region;
            List<IGH_DocumentObject> focusObjs = null;
            string how;
            if (ViewTool.IsCells(focus)) { region = CellRect(focus); how = "region " + focus; }
            else if (!string.IsNullOrEmpty(focus) && focus != "all")
            {
                focusObjs = GhQuery.Select(d, focus, out var notes);
                if (focusObjs.Count == 0) throw new NxError($"focus '{focus}' matched nothing on the canvas");
                region = Layout.Bounds(focusObjs);
                foreach (var g in d.Objects.OfType<GH_Group>().Where(g => g.ObjectIDs.Any(id => focusObjs.Any(o => o.InstanceGuid == id))))
                    if (g.ObjectIDs.All(id => focusObjs.Any(o => o.InstanceGuid == id))) region = RectangleF.Union(region, Layout.GroupRect(d, g));
                how = $"focus {focusObjs.Count} object(s)";
            }
            else
            {
                region = Layout.Bounds(d.Objects);
                foreach (var g in d.Objects.OfType<GH_Group>()) region = RectangleF.Union(region, Layout.GroupRect(d, g));
                how = "whole canvas";
            }
            region.Inflate(30, 30);
            if (!a.Has("size"))
            {   // no size given: fit the image aspect to the content so long graphs stay readable
                float ca = region.Width / Math.Max(1, region.Height);
                w = 1000; h = (int)Math.Clamp(1000 / ca, 260, 750);
                if (ca < 1000f / 750) { h = 750; w = (int)Math.Clamp(750 * ca, 400, 1000); }
            }
            // keep the output aspect: grow the region to w:h
            float aspect = w / (float)h;
            if (region.Width / region.Height > aspect) { float nh = region.Width / aspect; region.Y -= (nh - region.Height) / 2; region.Height = nh; }
            else { float nw = region.Height * aspect; region.X -= (nw - region.Width) / 2; region.Width = nw; }
            float zoom = w / region.Width;
            if (zoom > 1.6f)
            {   // do not blow tiny regions up beyond readable size; centre instead
                float nw = w / 1.6f, nh = h / 1.6f;
                region = new RectangleF(region.X + region.Width / 2 - nw / 2, region.Y + region.Height / 2 - nh / 2, nw, nh);
                zoom = 1.6f;
            }

            Bitmap bmp = null;
            string renderer = "gh";
            try { bmp = RenderGh(canvas, region, w, h, zoom); }
            catch (Exception e) { renderer = "schematic (" + e.GetType().Name + ")"; }
            if (bmp == null || IsBlank(bmp)) { bmp?.Dispose(); bmp = RenderSchematic(d, region, w, h, zoom); if (renderer == "gh") renderer = "schematic"; }

            Func<PointF, PointF> toImg = p => new PointF((p.X - region.X) * zoom, (p.Y - region.Y) * zoom);
            var visible = d.Objects.Where(o => !(o is GH_Group) && o.Attributes != null && region.IntersectsWith(o.Attributes.Bounds)).ToList();
            var toTag = focusObjs != null ? visible.Where(o => focusObjs.Contains(o)).ToList() : visible;
            bool readable = zoom >= 0.45f;
            bool doTags = tagMode == "on" || tagMode == "all" || (tagMode == "auto" && toTag.Count <= tagLimit && readable);
            if (tagMode == "off" || tagMode == "none") doTags = false;

            var cellCount = new Dictionary<string, int>();
            foreach (var o in visible)
            {
                var c = toImg(new PointF(o.Attributes.Bounds.X + o.Attributes.Bounds.Width / 2, o.Attributes.Bounds.Y + o.Attributes.Bounds.Height / 2));
                var cell = GridOverlay.CellOf(c.X / w, c.Y / h);
                cellCount[cell] = cellCount.TryGetValue(cell, out var k) ? k + 1 : 1;
            }

            List<string> placed;
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (grid) GridOverlay.Draw(g, w, h);
                using var font = new Font("Segoe UI", 8f, FontStyle.Bold);
                using var gfont = new Font("Segoe UI", 9.5f, FontStyle.Bold);
                var placer = new LabelPlacer(g, new RectangleF(1, 1, w - 2, h - 2), font);
                // components block their own boxes so tags sit beside them, not on them
                foreach (var o in visible) { var b = o.Attributes.Bounds; var p0 = toImg(b.Location); placer.Block(new RectangleF(p0.X, p0.Y, b.Width * zoom, b.Height * zoom)); }
                var items = new List<LabelItem>();
                // group labels (always; they are the region-level map)
                foreach (var grp in d.Objects.OfType<GH_Group>())
                {
                    var gr = Layout.GroupRect(d, grp);
                    if (!region.IntersectsWith(gr)) continue;
                    var p = toImg(gr.Location);
                    var txt = $"{GhCore.Handle(d, grp)} {grp.NickName}";
                    var it = new LabelItem { Text = txt, Color = Color.FromArgb(40, 40, 40), Anchor = p, Priority = -1 };
                    it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.X + txt.Length * 3.3f + 4, p.Y - 8), Angle = 0 });
                    it.Candidates.Add(new LabelCandidate { Pos = new PointF(p.X + txt.Length * 3.3f + 4, p.Y + 9), Angle = 0 });
                    items.Add(it);
                }
                if (doTags)
                {
                    foreach (var o in toTag)
                    {
                        var b = o.Attributes.Bounds;
                        var tl = toImg(b.Location); var br = toImg(new PointF(b.Right, b.Bottom));
                        var hnd = GhCore.Handle(d, o);
                        var col = Color.FromArgb(20, 60, 170);
                        if (o is IGH_ActiveObject ao)
                        {
                            if (ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error) col = Color.FromArgb(200, 20, 20);
                            else if (ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning) col = Color.FromArgb(200, 110, 0);
                        }
                        float tw = hnd.Length * 3.4f + 2;
                        var it = new LabelItem { Text = hnd, Color = col, Anchor = new PointF(tl.X, tl.Y), Priority = 0 };
                        it.Candidates.Add(new LabelCandidate { Pos = new PointF(tl.X + tw, tl.Y - 7), Angle = 0 });          // above-left
                        it.Candidates.Add(new LabelCandidate { Pos = new PointF(br.X - tw, tl.Y - 7), Angle = 0 });          // above-right
                        it.Candidates.Add(new LabelCandidate { Pos = new PointF(tl.X + tw, br.Y + 7), Angle = 0 });          // below-left
                        it.Candidates.Add(new LabelCandidate { Pos = new PointF(tl.X - tw - 2, (tl.Y + br.Y) / 2), Angle = 0 }); // left
                        items.Add(it);
                    }
                }
                placer.PlaceAndDraw(g, items);
                placed = placer.Placed;
            }
            var path = ViewTool.SaveImage(bmp, "gh");
            bmp.Dispose();
            r.Images.Add(path);
            Record = new Dictionary<string, object> { ["region"] = region, ["w"] = w, ["h"] = h };

            var errs = visible.Where(o => o is IGH_ActiveObject ao && ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error).Select(o => GhCore.Handle(d, o)).ToList();
            r.Line($"gh canvas {w}x{h}, {how}, zoom {zoom:0.##} ({renderer}). Grid A-H x 1-8. {visible.Count} object(s) visible.");
            if (doTags) r.Line("tagged: " + ViewTool.Ranges(placed.Where(Handles.IsGhHandle)));
            else
            {
                r.Line("cells (count): " + string.Join(" ", cellCount.OrderBy(kv => kv.Key[1]).ThenBy(kv => kv.Key[0]).Select(kv => $"{kv.Key}:{kv.Value}")));
                r.Line(readable ? $"too many to tag ({toTag.Count}); next: view {{target:'gh', focus:'C3:E5'}} or focus:'group:Name'"
                                : "zoomed out (text unreadable); next: view {target:'gh', focus:'C3:E5'} or focus a group handle");
            }
            if (errs.Count > 0) r.Line("errors (red): " + ViewTool.Ranges(errs));
        }

        static Bitmap RenderGh(GH_Canvas canvas, RectangleF region, int w, int h, float zoom)
        {
            var vp = new GH_Viewport(canvas.Viewport);
            vp.Width = w;
            vp.Height = h;
            vp.Zoom = zoom;
            vp.MidPoint = new PointF(region.X + region.Width / 2, region.Y + region.Height / 2);
            vp.ComputeProjection();
            return canvas.GenerateHiResImageTile(vp, Color.FromArgb(212, 208, 200));
        }

        static bool IsBlank(Bitmap b)
        {
            var c0 = b.GetPixel(0, 0);
            for (int y = 0; y < b.Height; y += Math.Max(1, b.Height / 24))
                for (int x = 0; x < b.Width; x += Math.Max(1, b.Width / 24))
                    if (b.GetPixel(x, y).ToArgb() != c0.ToArgb()) return false;
            return true;
        }

        /// Fallback: boxes, names, ports and wires drawn by us.
        static Bitmap RenderSchematic(GH_Document d, RectangleF region, int w, int h, float zoom)
        {
            var bmp = new Bitmap(w, h);
            using var g = Graphics.FromImage(bmp);
            g.Clear(Color.FromArgb(212, 208, 200));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(zoom, zoom);
            g.TranslateTransform(-region.X, -region.Y);
            foreach (var grp in d.Objects.OfType<GH_Group>())
            {
                var r = Layout.GroupRect(d, grp);
                using var b = new SolidBrush(Color.FromArgb(90, grp.Colour));
                g.FillRectangle(b, r);
                g.DrawString(grp.NickName, new Font("Segoe UI", 10, FontStyle.Bold), Brushes.Black, r.X + 4, r.Y + 2);
            }
            using (var wp = new Pen(Color.FromArgb(120, 0, 0, 0), 1.5f))
                foreach (var o in d.Objects)
                {
                    var ins = o is IGH_Component c ? c.Params.Input : o is IGH_Param p ? new List<IGH_Param> { p } : new List<IGH_Param>();
                    foreach (var ip in ins)
                        foreach (var s in ip.Sources)
                        {
                            if (s.Attributes == null || ip.Attributes == null) continue;
                            var a0 = s.Attributes.OutputGrip; var a1 = ip.Attributes.InputGrip;
                            float dx = Math.Max(20, (a1.X - a0.X) / 2);
                            g.DrawBezier(wp, a0, new PointF(a0.X + dx, a0.Y), new PointF(a1.X - dx, a1.Y), a1);
                        }
                }
            using var f = new Font("Segoe UI", 8);
            foreach (var o in d.Objects.Where(o => !(o is GH_Group) && o.Attributes != null))
            {
                var r = o.Attributes.Bounds;
                var fill = o is IGH_ActiveObject ao && ao.RuntimeMessageLevel == GH_RuntimeMessageLevel.Error ? Color.FromArgb(240, 140, 120)
                         : o is IGH_ActiveObject ao2 && ao2.RuntimeMessageLevel == GH_RuntimeMessageLevel.Warning ? Color.FromArgb(250, 200, 110) : Color.FromArgb(235, 235, 235);
                using (var b = new SolidBrush(fill)) g.FillRectangle(b, r);
                g.DrawRectangle(Pens.Black, r.X, r.Y, r.Width, r.Height);
                var label = o is GH_NumberSlider s ? $"{o.NickName} = {s.CurrentValue}" : o is GH_Panel pn ? Fmt.Trunc(pn.UserText, 40) : o.NickName;
                g.DrawString(label, f, Brushes.Black, r.X + 3, r.Y + 2);
                if (o is IGH_Component c)
                {
                    foreach (var ip in c.Params.Input) if (ip.Attributes != null) g.DrawString(ip.NickName, f, Brushes.DimGray, ip.Attributes.Bounds.X + 1, ip.Attributes.Bounds.Y);
                    foreach (var op in c.Params.Output) if (op.Attributes != null) g.DrawString(op.NickName, f, Brushes.DimGray, op.Attributes.Bounds.X + 1, op.Attributes.Bounds.Y);
                }
            }
            return bmp;
        }
    }

    /// Unbaked GH geometry seen in the Rhino viewport: zoom to the preview box of the given components,
    /// tag them with their g-handles drawn in the image only (the Rhino doc is never touched).
    public static class GhPreviewView
    {
        public static void Run(Args a, Reply r)
        {
            var d = GhCore.Doc(false) ?? throw new NxError("no GH document open");
            var focus = a.Str("focus", null);
            var objs = string.IsNullOrEmpty(focus) || ViewTool.IsCells(focus) ? d.Objects.ToList() : GhQuery.Select(d, focus, out _);
            var prev = objs.OfType<IGH_PreviewObject>().Where(p => p.IsPreviewCapable && !p.Hidden && p.ClippingBox.IsValid).ToList();
            if (prev.Count == 0) throw new NxError("no visible GH preview geometry for that focus", "check preview is on (not hidden) and the component produced geometry");
            var bb = BoundingBox.Empty;
            foreach (var p in prev) bb.Union(p.ClippingBox);
            // GH only draws previews while its editor is visible, so draw the output geometry ourselves
            var geo = new List<GeometryBase>();
            foreach (var p in prev)
            {
                var outs = p is IGH_Component c ? c.Params.Output : p is IGH_Param pp ? new[] { pp } : Enumerable.Empty<IGH_Param>();
                foreach (var op in outs)
                    foreach (var goo in op.VolatileData.AllData(true))
                    {
                        if (geo.Count > 5000) break;
                        var g = Grasshopper.Kernel.GH_Convert.ToGeometryBase(goo);
                        if (g != null) geo.Add(g);
                    }
            }
            ViewTool.RhinoCapture(a, r, (doc, ctx) =>
            {
                ctx.Geometry = geo;
                if (!ViewTool.IsCells(focus)) ctx.ZoomBox = bb;
                ctx.SkipRhinoTags = !a.Bool("rhino_tags", false);
                ctx.Tags = prev.Select(p => (p.ClippingBox.Center, GhCore.Handle(d, (IGH_DocumentObject)p), Color.FromArgb(0, 120, 60))).ToList();
                ctx.Text = $"GH preview of {prev.Count} component(s): " + ViewTool.Ranges(prev.Select(p => GhCore.Handle(d, (IGH_DocumentObject)p)));
            });
        }
    }
}
