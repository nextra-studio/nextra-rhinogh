using System;
using System.Collections.Generic;
using System.IO;
using Eto.Drawing;
using Eto.Forms;

namespace Nextra.Host
{
    /// A snip of the screen for the assistant's chat, like the system's snipping tool, on every monitor: all screens freeze
    /// and dim, the person drags a box over the part they mean on any of them, and that part comes back as a PNG with its
    /// place on the screen (so the engine can say which Rhino objects are in it). Esc or a right click cancels.
    /// Windows: one overlay per monitor over a frozen picture of it (PlatformQuirks.GrabScreens). Mac: the system's own
    /// interactive capture (PlatformQuirks.NativeSnip, `screencapture -i`, all screens too), without the place.
    static class Snip
    {
        public sealed class Result
        {
            public byte[] Png;
            /// the snip on the screen in the grab's pixels; null when unknown (Mac)
            public System.Drawing.Rectangle? Screen;
        }

        /// UI thread. `done` gets null when the person cancels.
        public static void Start(Action<Result> done)
        {
            var native = PlatformQuirks.NativeSnip(out bool handled);
            if (handled) { done(native == null ? null : new Result { Png = native }); return; }

            var grabs = PlatformQuirks.GrabScreens();
            if (grabs.Count == 0) { done(null); return; }

            var forms = new List<Form>();
            var frozen = new List<Bitmap>();
            bool finished = false;
            // the drag: the monitor it started on, and its corners in that monitor's pixels
            int on = -1;
            PointF? a = null, b = null;

            void Finish(Result r)
            {
                if (finished) return;
                finished = true;
                foreach (var f in forms) { try { f.Close(); } catch { } }
                foreach (var bmp in frozen) bmp.Dispose();
                done(r);
            }

            for (int i = 0; i < grabs.Count; i++)
            {
                int mi = i;
                var (png, phys, rhinoRect) = grabs[i];
                Bitmap pic;
                using (var ms = new MemoryStream(png)) pic = new Bitmap(ms);
                frozen.Add(pic);
                var form = new Form
                {
                    WindowStyle = WindowStyle.None, Resizable = false, Topmost = true, ShowInTaskbar = false, Minimizable = false, Maximizable = false,
                    BackgroundColor = Colors.Black, Title = "Nexiy snip",
                };
                var canvas = new Drawable { Cursor = Cursors.Crosshair, CanFocus = true };

                // the mouse in this monitor's pixels (the real cursor on Windows; Eto's position scaled where there is none)
                PointF At(MouseEventArgs e)
                {
                    var c = PlatformQuirks.CursorScreen();
                    if (c != null) return new PointF(c.Value.X - phys.X, c.Value.Y - phys.Y);
                    var k0 = phys.Width / (float)Math.Max(1, canvas.ClientSize.Width);
                    return new PointF(e.Location.X * k0, e.Location.Y * k0);
                }
                RectangleF SelPhys() => on != mi || a == null || b == null ? RectangleF.Empty
                    : new RectangleF(Math.Min(a.Value.X, b.Value.X), Math.Min(a.Value.Y, b.Value.Y), Math.Abs(b.Value.X - a.Value.X), Math.Abs(b.Value.Y - a.Value.Y));

                canvas.Paint += (s, e) =>
                {
                    var g = e.Graphics;
                    var size = canvas.ClientSize;
                    g.DrawImage(pic, 0, 0, size.Width, size.Height);
                    var k1 = size.Width / (float)Math.Max(1, phys.Width);
                    var sp = SelPhys();
                    var sel = new RectangleF(sp.X * k1, sp.Y * k1, sp.Width * k1, sp.Height * k1);
                    var dim = new Color(0, 0, 0, 0.30f);
                    if (sel.Width < 1 || sel.Height < 1) g.FillRectangle(dim, 0, 0, size.Width, size.Height);
                    else
                    {
                        g.FillRectangle(dim, 0, 0, size.Width, sel.Top);
                        g.FillRectangle(dim, 0, sel.Bottom, size.Width, size.Height - sel.Bottom);
                        g.FillRectangle(dim, 0, sel.Top, sel.Left, sel.Height);
                        g.FillRectangle(dim, sel.Right, sel.Top, size.Width - sel.Right, sel.Height);
                        g.DrawRectangle(new Pen(Colors.White, 1.5f), sel);
                        g.DrawRectangle(new Pen(Color.FromArgb(166, 229, 83), 1f), RectangleF.Inflate(sel, 1.5f, 1.5f));
                        var dims = $"{(int)sp.Width} × {(int)sp.Height}";
                        var font = new Font(SystemFont.Default, 9);
                        var ts = g.MeasureString(font, dims);
                        var ty = sel.Top - ts.Height - 8 >= 0 ? sel.Top - ts.Height - 8 : sel.Bottom + 6;
                        g.FillRectangle(new Color(0, 0, 0, 0.7f), sel.Left, ty, ts.Width + 12, ts.Height + 4);
                        g.DrawText(font, Colors.White, sel.Left + 6, ty + 2, dims);
                    }
                    // the hint, top middle of every screen
                    var hint = "Drag over the part you mean, on any screen  ·  Esc to cancel";
                    var hf = new Font(SystemFont.Default, 10);
                    var hs = g.MeasureString(hf, hint);
                    var hx = (size.Width - hs.Width) / 2 - 14;
                    g.FillRectangle(new Color(0.11f, 0.16f, 0.13f, 0.88f), hx, 18, hs.Width + 28, hs.Height + 14);
                    g.DrawText(hf, Colors.White, hx + 14, 25, hint);
                };

                canvas.MouseDown += (s, e) =>
                {
                    if (e.Buttons == MouseButtons.Alternate) { Finish(null); return; }
                    if (e.Buttons != MouseButtons.Primary) return;
                    e.Handled = true;
                    int was = on;
                    on = mi; a = At(e); b = a;
                    if (was >= 0 && was != mi && was < forms.Count) forms[was].Content?.Invalidate();
                    canvas.Invalidate();
                };
                canvas.MouseMove += (s, e) => { if (on == mi && a != null && e.Buttons == MouseButtons.Primary) { b = At(e); canvas.Invalidate(); } };
                canvas.MouseUp += (s, e) =>
                {
                    if (on != mi || a == null) return;
                    b = At(e);
                    var sel = SelPhys();
                    if (sel.Width < 8 || sel.Height < 8) { a = b = null; canvas.Invalidate(); return; }   // a click: try again
                    // the box in this monitor's pixels, cut out of its frozen picture
                    var x = (int)Math.Round(sel.X); var y = (int)Math.Round(sel.Y);
                    var w = (int)Math.Round(sel.Width); var h = (int)Math.Round(sel.Height);
                    var cut = PlatformQuirks.CropPng(png, x, y, w, h);
                    // the place in Rhino's own coordinates (stretched on a monitor with another scale), for the object list
                    float kx = rhinoRect.Width / (float)phys.Width, ky = rhinoRect.Height / (float)phys.Height;
                    var place = new System.Drawing.Rectangle(rhinoRect.X + (int)(x * kx), rhinoRect.Y + (int)(y * ky), (int)(w * kx), (int)(h * ky));
                    Finish(cut == null ? null : new Result { Png = cut, Screen = place });
                };
                form.KeyDown += (s, e) => { if (e.Key == Keys.Escape) { e.Handled = true; Finish(null); } };
                canvas.KeyDown += (s, e) => { if (e.Key == Keys.Escape) { e.Handled = true; Finish(null); } };
                form.Closed += (s, e) => { if (!finished) Finish(null); };     // closed from outside: cancel all
                form.Content = canvas;
                forms.Add(form);
            }

            for (int i = 0; i < forms.Count; i++)
            {
                forms[i].Show();
                PlatformQuirks.PlaceOn(forms[i], grabs[i].phys);     // exactly over its monitor
                forms[i].BringToFront();
            }
            // the keyboard to the overlay under the mouse (Esc must work without a click first)
            var cur = PlatformQuirks.CursorScreen();
            int focus = 0;
            for (int i = 0; i < grabs.Count; i++) if (cur != null && grabs[i].phys.Contains(cur.Value)) focus = i;
            PlatformQuirks.Foreground(forms[focus]);
            forms[focus].Focus();
            forms[focus].Content?.Focus();

            // Esc wherever the keyboard focus is; and never left on the screen: a snip nobody finishes cancels after 2 minutes
            var esc = new UITimer { Interval = 0.05 };
            esc.Elapsed += (s, e) => { if (finished) { esc.Stop(); return; } if (PlatformQuirks.EscDown()) { esc.Stop(); Finish(null); } };
            esc.Start();
            var guard = new UITimer { Interval = 120 };
            guard.Elapsed += (s, e) => { guard.Stop(); if (!finished) Finish(null); };
            guard.Start();
        }
    }
}
