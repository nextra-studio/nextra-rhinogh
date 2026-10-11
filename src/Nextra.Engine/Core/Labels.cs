using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace Nextra.Engine
{
    /// Screen-space label placement shared by Rhino and GH views.
    /// Labels are plain text (no dots/bubbles) rotated along the element, with a white halo,
    /// tried at several candidate spots; placed boxes never overlap each other.
    public class LabelCandidate
    {
        public PointF Pos;        // text center
        public float Angle;       // degrees, already normalised to [-90, 90]
        public PointF? Leader;    // draw a leader line to this anchor
        public bool Hidden;       // anchor is behind other geometry
    }

    public class LabelItem
    {
        public string Text;
        public Color Color;
        public List<LabelCandidate> Candidates = new List<LabelCandidate>();
        public PointF Anchor;     // used for fallback leader search
        public float Priority;    // lower = placed first
    }

    public class LabelPlacer
    {
        readonly List<PointF[]> _placed = new List<PointF[]>();
        readonly RectangleF _bounds;
        readonly Font _font;
        readonly Graphics _measure;
        public readonly List<string> Unplaced = new List<string>();
        public readonly List<string> Placed = new List<string>();
        public readonly List<string> Behind = new List<string>();

        public LabelPlacer(Graphics g, RectangleF bounds, Font font)
        {
            _measure = g; _bounds = bounds; _font = font;
        }

        public void Block(RectangleF r) => _placed.Add(Corners(new PointF(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width, r.Height, 0));

        public static float NormAngle(float deg)
        {
            while (deg > 90) deg -= 180;
            while (deg < -90) deg += 180;
            return deg;
        }

        static PointF[] Corners(PointF c, float w, float h, float deg)
        {
            double a = deg * Math.PI / 180;
            float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
            var pts = new[] { new PointF(-w / 2, -h / 2), new PointF(w / 2, -h / 2), new PointF(w / 2, h / 2), new PointF(-w / 2, h / 2) };
            return pts.Select(p => new PointF(c.X + p.X * cs - p.Y * sn, c.Y + p.X * sn + p.Y * cs)).ToArray();
        }

        static bool Overlap(PointF[] a, PointF[] b)
        {
            // separating axis test for two convex quads
            foreach (var poly in new[] { a, b })
                for (int i = 0; i < 4; i++)
                {
                    var p1 = poly[i]; var p2 = poly[(i + 1) % 4];
                    float nx = p2.Y - p1.Y, ny = p1.X - p2.X;
                    float minA = float.MaxValue, maxA = float.MinValue, minB = float.MaxValue, maxB = float.MinValue;
                    foreach (var p in a) { var d = nx * p.X + ny * p.Y; minA = Math.Min(minA, d); maxA = Math.Max(maxA, d); }
                    foreach (var p in b) { var d = nx * p.X + ny * p.Y; minB = Math.Min(minB, d); maxB = Math.Max(maxB, d); }
                    if (maxA < minB || maxB < minA) return false;
                }
            return true;
        }

        bool Inside(PointF[] q) => q.All(p => _bounds.Contains(p));

        public void PlaceAndDraw(Graphics g, IEnumerable<LabelItem> items)
        {
            foreach (var it in items.OrderBy(i => i.Priority))
            {
                var sz = _measure.MeasureString(it.Text, _font);
                float w = sz.Width * 0.92f, h = sz.Height * 0.8f;
                LabelCandidate chosen = null; PointF[] quad = null;
                // visible spots first; if every spot is occluded the label is drawn faded with '*'
                bool allHidden = it.Candidates.Count > 0 && it.Candidates.All(c => c.Hidden);
                foreach (var c in it.Candidates.OrderBy(c => c.Hidden ? 1 : 0))
                {
                    var q = Corners(c.Pos, w + 2, h, c.Angle);
                    if (!Inside(q)) continue;
                    if (_placed.Any(p => Overlap(p, q))) continue;
                    chosen = c; quad = q; break;
                }
                if (chosen == null)
                {
                    // fallback: horizontal label on rings around the anchor, with a leader line
                    for (float r = 18; r <= 90 && chosen == null; r += 12)
                        for (int k = 0; k < 12 && chosen == null; k++)
                        {
                            double a = k * Math.PI / 6 - Math.PI / 4;
                            var pos = new PointF(it.Anchor.X + (float)Math.Cos(a) * (r + w / 2), it.Anchor.Y + (float)Math.Sin(a) * (r + h / 2));
                            var q = Corners(pos, w + 2, h, 0);
                            if (!Inside(q) || _placed.Any(p => Overlap(p, q))) continue;
                            chosen = new LabelCandidate { Pos = pos, Angle = 0, Leader = it.Anchor }; quad = q;
                        }
                }
                if (chosen == null) { Unplaced.Add(it.Text); continue; }
                _placed.Add(quad);
                Placed.Add(it.Text);
                if (allHidden) Behind.Add(it.Text);
                Draw(g, allHidden ? it.Text + "*" : it.Text, allHidden ? Color.FromArgb(140, it.Color) : it.Color, chosen, w, h);
            }
        }

        void Draw(Graphics g, string text, Color color, LabelCandidate c, float w, float h)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (c.Leader.HasValue)
            {
                using var lp = new Pen(Color.FromArgb(200, color), 1f);
                var a = c.Leader.Value;
                // leader from the nearest point of the label box
                var end = new PointF(Math.Clamp(a.X, c.Pos.X - w / 2, c.Pos.X + w / 2), Math.Clamp(a.Y, c.Pos.Y - h / 2, c.Pos.Y + h / 2));
                g.DrawLine(lp, a, end);
                g.FillEllipse(new SolidBrush(color), a.X - 1.5f, a.Y - 1.5f, 3, 3);
            }
            g.TranslateTransform(c.Pos.X, c.Pos.Y);
            g.RotateTransform(c.Angle);
            using (var path = new GraphicsPath())
            {
                float emSize = g.DpiY * _font.SizeInPoints / 72f;
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                path.AddString(text, _font.FontFamily, (int)_font.Style, emSize, new PointF(0, 0), sf);
                using var halo = new Pen(Color.FromArgb(235, 255, 255, 255), 3.2f) { LineJoin = LineJoin.Round };
                g.DrawPath(halo, path);
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);
            }
            g.Restore(state);
        }
    }

    /// 8×8 grid overlay: columns A–H (left→right), rows 1–8 (top→bottom).
    public static class GridOverlay
    {
        public const int N = 8;
        public static string Col(int i) => ((char)('A' + i)).ToString();

        public static void Draw(Graphics g, int w, int h)
        {
            g.SmoothingMode = SmoothingMode.None;
            using var pen = new Pen(Color.FromArgb(70, 90, 90, 140), 1f) { DashStyle = DashStyle.Dash };
            for (int i = 1; i < N; i++)
            {
                g.DrawLine(pen, i * w / (float)N, 0, i * w / (float)N, h);
                g.DrawLine(pen, 0, i * h / (float)N, w, i * h / (float)N);
            }
            using var f = new Font("Segoe UI", 7.5f, FontStyle.Bold);
            using var b = new SolidBrush(Color.FromArgb(150, 70, 70, 120));
            for (int i = 0; i < N; i++)
            {
                g.DrawString(Col(i), f, b, i * w / (float)N + 2, 1);
                g.DrawString((i + 1).ToString(), f, b, 1, i * h / (float)N + 10);
            }
        }

        /// "C3" or "C3:E5" → normalised rect (0..1) in image space.
        public static RectangleF Parse(string cells)
        {
            var parts = cells.Trim().ToUpperInvariant().Split(':');
            var a = Cell(parts[0]);
            var b = parts.Length > 1 ? Cell(parts[1]) : a;
            int c0 = Math.Min(a.c, b.c), c1 = Math.Max(a.c, b.c), r0 = Math.Min(a.r, b.r), r1 = Math.Max(a.r, b.r);
            return new RectangleF(c0 / (float)N, r0 / (float)N, (c1 - c0 + 1) / (float)N, (r1 - r0 + 1) / (float)N);
        }

        static (int c, int r) Cell(string s)
        {
            if (s.Length < 2 || s[0] < 'A' || s[0] > 'A' + N - 1 || !int.TryParse(s.Substring(1), out int r) || r < 1 || r > N)
                throw new NxError($"bad cell '{s}'", "cells are A1..H8 (column letter, row number), ranges like C3:E5");
            return (s[0] - 'A', r - 1);
        }

        public static string CellOf(float nx, float ny)
        {
            int c = Math.Clamp((int)(nx * N), 0, N - 1), r = Math.Clamp((int)(ny * N), 0, N - 1);
            return Col(c) + (r + 1);
        }
    }
}
