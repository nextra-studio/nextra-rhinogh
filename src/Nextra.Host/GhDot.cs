using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Waits (via a WinForms timer) until Grasshopper is loaded, then draws the same status dot
    /// in the bottom-right corner of the GH canvas. No Grasshopper type is touched before GH itself is loaded.
    static class GhDotHook
    {
        static Timer _timer;
        static bool _attached;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Timer { Interval = 1500 };
            _timer.Tick += (s, e) => TryAttach();
            _timer.Start();
        }

        public static void Stop()
        {
            try { _timer?.Stop(); _timer?.Dispose(); } catch { }
            _timer = null;
            if (_attached) { try { GhDot.Detach(); } catch { } _attached = false; }
        }

        static void TryAttach()
        {
            bool ghLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Grasshopper");
            if (!ghLoaded) return;
            try
            {
                if (_attached) { GhDot.Refresh(); return; }
                _attached = GhDot.Attach();
            }
            catch (Exception e) { RhinoApp.WriteLine("Nextra: GH dot failed: " + e.Message); _timer?.Stop(); }
        }
    }

    static class GhDot
    {
        static Grasshopper.GUI.Canvas.GH_Canvas _canvas;
        static Color _last;

        static Rectangle DotRect(Control c) => new Rectangle(c.ClientSize.Width - 22, c.ClientSize.Height - 22, 12, 12);

        public static bool Attach()
        {
            var cv = Grasshopper.Instances.ActiveCanvas;
            if (cv == null) return false;
            _canvas = cv;
            cv.CanvasPostPaintWidgets += OnPaint;
            cv.MouseDown += OnMouseDown;
            cv.Refresh();
            return true;
        }

        public static void Detach()
        {
            if (_canvas == null) return;
            _canvas.CanvasPostPaintWidgets -= OnPaint;
            _canvas.MouseDown -= OnMouseDown;
            _canvas.Refresh();
            _canvas = null;
        }

        public static void Refresh()
        {
            StatusDot.CurrentState(out var c, out _);
            if (c != _last && _canvas != null) { _last = c; _canvas.Invalidate(DotRect(_canvas)); }
        }

        static void OnPaint(Grasshopper.GUI.Canvas.GH_Canvas sender)
        {
            var g = sender.Graphics;
            if (g == null) return;
            var saved = g.Transform;
            g.ResetTransform();
            StatusDot.CurrentState(out var c, out _);
            _last = c;
            var r = DotRect(sender);
            var sm = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(c)) g.FillEllipse(b, r);
            using (var p = new Pen(Color.FromArgb(60, 60, 60), 1.5f)) g.DrawEllipse(p, r);
            g.SmoothingMode = sm;
            g.Transform = saved;
        }

        static void OnMouseDown(object sender, MouseEventArgs e)
        {
            var c = sender as Control;
            if (c == null || e.Button != MouseButtons.Left) return;
            var r = DotRect(c);
            r.Inflate(3, 3);
            if (!r.Contains(e.Location)) return;
            StatusDot.ClickAction();
            c.Invalidate();
        }
    }
}
