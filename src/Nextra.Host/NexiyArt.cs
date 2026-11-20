using System;
using Eto.Drawing;

namespace Nextra.Host
{
    /// What Nexiy looks like in one frame. Set by StatusDot (and the Grasshopper runner) from the state and the running act.
    class NexiyLook
    {
        public Color Body = NexiyArt.Lime;
        public NexiyFace Face = NexiyFace.Normal;
        public NexiyProp Prop = NexiyProp.None;
        public float Dx, Dy;          // whole figure offset (shake, hop), in art units
        public float Spin;            // degrees, about the figure's centre (a flip in the air)
        public bool Flip;             // facing right instead of left (mirrored)
        public float EyesDx;          // looking sideways
        public float ArmL, ArmR;      // arm lift in art units (0 = down; 12 = raised)
        public float LegL, LegR;      // leg lift (running / walking steps)
        public bool ThinkDots, DotsLit;
        public bool Sparks;           // just connected / done
        public bool Zzz;              // asleep (drawn only when big enough)
        public double Phase;          // 0..1 within the act, for props that move (steam, confetti, tears)
    }

    enum NexiyFace { Normal, Blink, Worried, Asleep, Cry, Eat }
    enum NexiyProp { None, Laptop, Cup, Snack, Confetti, Glasses, Sunglasses }

    /// Nexiy, the Nextra engineer mascot, drawn with Eto shapes from the same geometry as the SVG in
    /// nextra.workspace/src/mascot.tsx (its art units: the figure spans about x 0..72, y 1..74), with the same acts:
    /// typing (laptop), reading (glasses), coffee, snack, party (confetti), crying, sleeping, looking, stretching, jumping.
    /// Plain rectangles and a few strokes, so it scales cleanly from 16 px to 96 px and is the same on Windows and Mac.
    static class NexiyArt
    {
        public static readonly Color Lime = Color.FromArgb(0xa6, 0xe5, 0x53);
        public static readonly Color Amber = Color.FromArgb(0xf0, 0xaa, 0x1e);
        public static readonly Color Red = Color.FromArgb(0xf2, 0x55, 0x5a);
        public static readonly Color Purple = Color.FromArgb(0xa8, 0x68, 0xe8);
        public static readonly Color Orange = Color.FromArgb(0xff, 0x90, 0x28);
        public static readonly Color Blue = Color.FromArgb(0x4d, 0xb6, 0xff);
        static readonly Color Ink = Color.FromArgb(0x1c, 0x2a, 0x22);
        static readonly Color White = Colors.White;

        /// Art-space box drawn into the window: room above the helmet for hops, the thinking dots and confetti; room on the
        /// right for the cup.
        public const float Left = -8, Top = -20, Width = 92, Height = 100;

        /// Window size (pixels) for a figure width of `size` pixels (the figure is 72 art units wide inside a 92-unit box).
        public static Size WindowSize(int size) => new Size((int)Math.Round(size * Width / 72), (int)Math.Round(size * Height / 72));

        public static void Draw(Graphics g, int size, NexiyLook k)
        {
            float s = size / 72f;
            bool detail = size >= 28;     // small triangles, Z, crumbs: only when they are more than a pixel
            g.AntiAlias = true;
            g.SaveTransform();
            g.ScaleTransform(s);
            g.TranslateTransform(-Left + k.Dx, -Top + k.Dy);
            if (k.Spin != 0 || k.Flip)
            {
                g.TranslateTransform(36, 40);
                if (k.Spin != 0) g.RotateTransform(k.Spin);
                if (k.Flip) g.ScaleTransform(-1, 1);
                g.TranslateTransform(-36, -40);
            }

            // helmet (white with a dark outline: outline first, fill on top, like paint-order: stroke)
            Box(g, White, 1.6f, 7.6f, 19, 57, 6);
            Box(g, White, 1.6f, 30, 1, 12, 16.4f);
            Box(g, White, 1.6f, 19.2f, 7, 6.4f, 12); Box(g, White, 1.6f, 15.6f, 10, 3.6f, 9); Box(g, White, 1.6f, 12.6f, 13, 3, 6);
            Box(g, White, 1.6f, 46.4f, 7, 6.4f, 12); Box(g, White, 1.6f, 52.8f, 10, 3.6f, 9); Box(g, White, 1.6f, 56.4f, 13, 3, 6);
            g.FillRectangle(White, 7.6f, 19, 57, 6); g.FillRectangle(White, 30, 1, 12, 18);
            g.FillRectangle(White, 19.2f, 7, 6.4f, 13); g.FillRectangle(White, 15.6f, 10, 3.6f, 10); g.FillRectangle(White, 12.6f, 13, 3, 7);
            g.FillRectangle(White, 46.4f, 7, 6.4f, 13); g.FillRectangle(White, 52.8f, 10, 3.6f, 10); g.FillRectangle(White, 56.4f, 13, 3, 7);

            // body: arms, legs, torso
            Box(g, k.Body, 1.2f, 0.4f, 41 - k.ArmL, 9.6f, 9.6f);
            Box(g, k.Body, 1.2f, 62, 41 - k.ArmR, 9.6f, 9.6f);
            Box(g, k.Body, 1.2f, 18, 64 - k.LegL, 9.2f, 10);
            Box(g, k.Body, 1.2f, 45, 64 - k.LegR, 9.2f, 10);
            Box(g, k.Body, 1.2f, 9.6f, 29.5f, 53, 35.4f);

            // face
            switch (k.Face)
            {
                case NexiyFace.Worried:
                    using (var p = RoundPen(Ink, 2.2f)) { g.DrawLine(p, 18.5f, 34.5f, 26, 32); g.DrawLine(p, 45.5f, 32, 53, 34.5f); }
                    g.FillRectangle(Ink, 21, 37.6f, 4.6f, 6.4f); g.FillRectangle(Ink, 46.4f, 37.6f, 4.6f, 6.4f);
                    using (var p = RoundPen(White, 2.4f)) g.DrawPath(p, Curve(24, 55, 32, 51, 40, 51, 48, 55));
                    break;
                case NexiyFace.Cry:
                {
                    // sad, not scary: brows up in the middle, eyes shut in soft arcs, tears running from them, a small wobbly frown
                    using (var p = RoundPen(Ink, 2.2f))
                    {
                        g.DrawLine(p, 18, 33, 26.5f, 31); g.DrawLine(p, 45.5f, 31, 54, 33);
                        g.DrawPath(p, Curve(19.5f, 39, 21.5f, 42.5f, 25.5f, 42.5f, 27.5f, 39));
                        g.DrawPath(p, Curve(44.5f, 39, 46.5f, 42.5f, 50.5f, 42.5f, 52.5f, 39));
                    }
                    var tear = Color.FromArgb(0x7c, 0xc6, 0xff);
                    var tearEdge = Color.FromArgb(0x3a, 0x8f, 0xd6);
                    float run = (float)(k.Phase * 9 % 9);
                    // streams down the cheeks, a drop at the end
                    g.FillRectangle(tear, 22, 43, 2.6f, 4 + run); g.FillRectangle(tear, 47.6f, 43, 2.6f, 4 + (run + 4) % 9);
                    g.FillEllipse(tearEdge, 21.3f, 46 + run, 4, 4.6f); g.FillEllipse(tearEdge, 46.9f, 46 + (run + 4) % 9, 4, 4.6f);
                    using (var p = RoundPen(White, 2.4f)) g.DrawPath(p, Curve(28, 57, 32, 53, 40, 53, 44, 57));
                    break;
                }
                case NexiyFace.Asleep:
                    using (var p = RoundPen(Ink, 2.4f)) { g.DrawLine(p, 20, 42, 26, 42); g.DrawLine(p, 45, 42, 51, 42); }
                    Smile(g, detail);
                    break;
                case NexiyFace.Blink:
                    g.FillRectangle(Ink, 21 + k.EyesDx, 40, 4.6f, 1.6f); g.FillRectangle(Ink, 46.4f + k.EyesDx, 40, 4.6f, 1.6f);
                    Smile(g, detail);
                    break;
                case NexiyFace.Eat:
                    g.FillRectangle(Ink, 21 + k.EyesDx, 36, 4.6f, 9.4f); g.FillRectangle(Ink, 46.4f + k.EyesDx, 36, 4.6f, 9.4f);
                    using (var p = RoundPen(White, 2.4f)) g.DrawPath(p, (k.Phase * 6 % 1) < .5 ? Curve(28, 52, 33, 56, 39, 56, 44, 52) : Curve(29, 53, 33, 54, 39, 54, 43, 53));
                    break;
                default:
                    g.FillRectangle(Ink, 21 + k.EyesDx, 36, 4.6f, 9.4f); g.FillRectangle(Ink, 46.4f + k.EyesDx, 36, 4.6f, 9.4f);
                    Smile(g, detail);
                    break;
            }

            // props of the act
            switch (k.Prop)
            {
                case NexiyProp.Laptop:
                    // on the lap, below the mouth, so the face stays readable at small sizes
                    using (var path = Poly(19, 70, 23, 58, 49, 58, 53, 70)) g.FillPath(Color.FromArgb(0x3b, 0x4f, 0x43), path);
                    if ((k.Phase * 8 % 1) < .6) g.FillRectangle(Lime, 26, 61, 20, 6);
                    g.FillRectangle(Color.FromArgb(0x2c, 0x3e, 0x33), 8, 70, 56, 4);
                    break;
                case NexiyProp.Cup:
                {
                    float lift = (float)Math.Max(0, Math.Sin(k.Phase * Math.PI * 2)) * 8;
                    Box(g, White, 1.4f, 66 - lift, 38 - lift, 11, 13);
                    using (var p = new Pen(Ink, 1.4f)) { g.DrawLine(p, 77 - lift, 41 - lift, 80 - lift, 41 - lift); g.DrawLine(p, 80 - lift, 41 - lift, 80 - lift, 47 - lift); g.DrawLine(p, 80 - lift, 47 - lift, 77 - lift, 47 - lift); }
                    if (detail) using (var p = RoundPen(Color.FromArgb(150, 0x9f, 0xb1, 0xa5), 1.4f)) { float st = (float)(k.Phase * 3 % 1) * 3; g.DrawLine(p, 69 - lift, 34 - lift - st, 70 - lift, 30 - lift - st); g.DrawLine(p, 74 - lift, 34 - lift - st, 75 - lift, 30 - lift - st); }
                    break;
                }
                case NexiyProp.Snack:
                    Circle(g, Color.FromArgb(0xd9, 0xa0, 0x4f), 36, 60, 6);
                    if (detail) { g.FillEllipse(Color.FromArgb(0x6b, 0x3d, 0x1a), 33, 57, 2, 2); g.FillEllipse(Color.FromArgb(0x6b, 0x3d, 0x1a), 37, 60, 2, 2); }
                    break;
                case NexiyProp.Glasses:
                    using (var p = new Pen(Ink, 2)) { g.DrawEllipse(p, 16.8f, 34, 13, 13); g.DrawEllipse(p, 42.2f, 34, 13, 13); g.DrawLine(p, 29.8f, 40.5f, 42.2f, 40.5f); }
                    break;
                case NexiyProp.Sunglasses:
                    g.FillRectangle(Ink, 15, 35, 17, 10); g.FillRectangle(Ink, 40, 35, 17, 10); g.FillRectangle(Ink, 31, 37, 10, 2.4f);
                    if (detail) g.FillRectangle(Color.FromArgb(180, 255, 255, 255), 18, 37, 5, 2);
                    break;
                case NexiyProp.Confetti:
                {
                    var cols = new[] { Orange, Blue, Purple, Red, Lime, Color.FromArgb(0xff, 0xd2, 0x3f) };
                    var at = new[] { (-6f, 4f), (76f, 8f), (-2f, -12f), (72f, -14f), (36f, -18f), (10f, -6f) };
                    for (int i = 0; i < 6; i++)
                    {
                        float t = (float)((k.Phase * 2 + i * .17) % 1);
                        g.SaveTransform();
                        g.TranslateTransform(at[i].Item1 + 2, at[i].Item2 + 8 - t * 18);
                        g.RotateTransform(t * 220);
                        g.FillRectangle(cols[i], -2, -2, 4, 4);
                        g.RestoreTransform();
                    }
                    break;
                }
            }

            if (k.Sparks)
                using (var p = new Pen(k.Body, 3.2f)) { g.DrawLine(p, 60, -4, 64, -10); g.DrawLine(p, 66, 4, 72, 0); }
            if (k.ThinkDots && k.DotsLit)
            {
                Box(g, White, 1, 62, -6, 6, 6);
                Box(g, White, 1, 58, 4, 3.6f, 3.6f);
            }
            if (k.Zzz && detail)
            {
                float zt = (float)(k.Phase % 1) * 6;
                using (var p = new Pen(Ink, 2)) { g.DrawLine(p, 64, -2 - zt, 71, -2 - zt); g.DrawLine(p, 71, -2 - zt, 64, 6 - zt); g.DrawLine(p, 64, 6 - zt, 71, 6 - zt); }
            }
            g.RestoreTransform();
        }

        /// one frame as PNG bytes (for the Grasshopper canvas, which draws with System.Drawing)
        public static byte[] Png(int size, NexiyLook k)
        {
            var ws = WindowSize(size);
            using var bmp = new Bitmap(ws.Width, ws.Height, PixelFormat.Format32bppRgba);
            using (var g = new Graphics(bmp)) { g.Clear(Colors.Transparent); Draw(g, size, k); }
            return bmp.ToByteArray(ImageFormat.Png);
        }

        static void Smile(Graphics g, bool detail)
        {
            using (var p = RoundPen(White, 2.4f)) g.DrawPath(p, Curve(15, 49.5f, 29, 54.5f, 43, 54.5f, 57, 49.5f));
            if (!detail) return;
            using (var p = RoundPen(White, 1.6f))
            {
                g.DrawPath(p, Triangle(19, 52.4f, 16, 58.4f, 22.4f, 58.4f));
                g.DrawPath(p, Triangle(53, 52.4f, 49.6f, 58.4f, 56, 58.4f));
            }
        }

        /// Filled rectangle with an outline of width `w` around it (outline under the fill, as in the SVG).
        static void Box(Graphics g, Color fill, float w, float x, float y, float width, float height)
        {
            float h = w / 2;
            g.FillRectangle(Ink, x - h, y - h, width + w, height + w);
            g.FillRectangle(fill, x, y, width, height);
        }

        static void Circle(Graphics g, Color fill, float cx, float cy, float r)
        {
            g.FillEllipse(Ink, cx - r - .6f, cy - r - .6f, 2 * r + 1.2f, 2 * r + 1.2f);
            g.FillEllipse(fill, cx - r, cy - r, 2 * r, 2 * r);
        }

        static Pen RoundPen(Color c, float w) => new Pen(c, w) { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        static GraphicsPath Curve(float x0, float y0, float x1, float y1, float x2, float y2, float x3, float y3)
        {
            var path = new GraphicsPath();
            path.AddBezier(new PointF(x0, y0), new PointF(x1, y1), new PointF(x2, y2), new PointF(x3, y3));
            return path;
        }

        /// a wobbly crying mouth
        static GraphicsPath Wave(float x, float y)
        {
            var path = new GraphicsPath();
            for (int i = 0; i < 4; i++) path.AddBezier(new PointF(x + i * 5, y), new PointF(x + i * 5 + 1.6f, y - 3), new PointF(x + i * 5 + 3.4f, y - 3), new PointF(x + i * 5 + 5, y));
            return path;
        }

        static GraphicsPath Poly(params float[] xy)
        {
            var path = new GraphicsPath();
            for (int i = 0; i + 3 < xy.Length; i += 2) path.AddLine(xy[i], xy[i + 1], xy[i + 2], xy[i + 3]);
            path.CloseFigure();
            return path;
        }

        static GraphicsPath Triangle(float x0, float y0, float x1, float y1, float x2, float y2)
        {
            var path = new GraphicsPath();
            path.AddLine(x0, y0, x1, y1);     // AddLine, not AddLines: its signature differs between Eto versions
            path.AddLine(x1, y1, x2, y2);
            path.CloseFigure();
            return path;
        }
    }
}
