using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Waits (via an Eto UI timer, same on Windows and Mac) until Grasshopper is loaded, then draws the same status dot
    /// in the bottom-right corner of the GH canvas. No Grasshopper type is touched before GH itself is loaded.
    /// The canvas itself is Grasshopper's API (GH_Canvas, a WinForms control on both systems), so this part keeps
    /// System.Drawing / WinForms types: they come from Grasshopper, not from us.
    static class GhDotHook
    {
        static Eto.Forms.UITimer _timer;
        static bool _attached;

        public static void Start()
        {
            if (_timer != null) return;
            _timer = new Eto.Forms.UITimer { Interval = 0.5 };   // also starts / stops the Chaos runner promptly
            _timer.Elapsed += (s, e) => TryAttach();
            _timer.Start();
        }

        public static void Stop()
        {
            try { _timer?.Stop(); } catch { }
            _timer = null;
            if (_attached) { try { GhDot.Detach(); } catch { } _attached = false; }
        }

        static void TryAttach()
        {
            bool ghLoaded = AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Grasshopper");
            if (!ghLoaded) return;
            try
            {
                if (_attached) { GhDot.Refresh(); GhDot.UpdateRunner(); return; }
                _attached = GhDot.Attach();
            }
            catch (Exception e) { RhinoApp.WriteLine("NextraRhinoGh: GH dot failed: " + e.Message); _timer?.Stop(); }
        }
    }

    static class GhDot
    {
        static Grasshopper.GUI.Canvas.GH_Canvas _canvas;
        static Color _last;

        static Color StateColor() { StatusDot.CurrentState(out var argb, out _); return Color.FromArgb(argb); }

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
            var c = StateColor();
            if (c != _last && _canvas != null) { _last = c; _canvas.Invalidate(DotRect(_canvas)); }
        }

        // ---- Chaos: Nexiy runs around the canvas edge while something works -------------------------------------------------
        static Eto.Forms.UITimer _run;
        static double _runT;
        static readonly Random Rnd = new Random();
        static StatusDot.RoamPath _path, _prev;
        static DateTime _pathStart, _pathUntil;
        static double _lastX;
        static bool _faceRight;
        static Rectangle _runRect = Rectangle.Empty;
        static readonly Bitmap[] Frames = new Bitmap[4];   // [flip*2 + step]
        const int RunSize = 36;

        static Bitmap Frame(bool flip, int step)
        {
            int i = (flip ? 2 : 0) + step;
            if (Frames[i] != null) return Frames[i];
            var look = new NexiyLook { Flip = flip, Dy = step == 0 ? -3 : 0, LegL = step == 0 ? 4 : 0, LegR = step == 1 ? 4 : 0, ArmL = step == 0 ? 8 : 0, ArmR = step == 1 ? 8 : 0 };
            using var ms = new System.IO.MemoryStream(NexiyArt.Png(RunSize, look));
            return Frames[i] = new Bitmap(ms);
        }

        /// called by the hook's timer: start or stop the runner
        public static void UpdateRunner()
        {
            bool want = _canvas != null && _canvas.Visible && _canvas.ClientSize.Width > 120 && _canvas.ClientSize.Height > 120 && StatusDot.Level >= 3 && StatusDot.Active;
            if (want && _run == null)
            {
                _run = new Eto.Forms.UITimer { Interval = 0.06 };
                _run.Elapsed += (s, e) => StepRunner();
                _run.Start();
            }
            else if (!want && _run != null)
            {
                _run.Stop(); _run = null;
                if (_canvas != null && !_runRect.IsEmpty) _canvas.Invalidate(Inflate(_runRect));
                _runRect = Rectangle.Empty;
            }
        }

        static Rectangle Inflate(Rectangle r) { r.Inflate(4, 4); return r; }

        static void StepRunner()
        {
            if (_canvas == null || !_canvas.Visible || _canvas.ClientSize.Width <= 120 || !StatusDot.Active || StatusDot.Level < 3) { _canvas?.Invalidate(); UpdateRunner(); return; }
            _runT += 0.06 * StatusDot.Speed;
            var now = DateTime.Now;
            if (_path == null || now > _pathUntil) { _prev = _path; _path = StatusDot.RandomPath(Rnd); _pathStart = now; _pathUntil = now.AddSeconds(8 + Rnd.NextDouble() * 6); }
            var c = _canvas.ClientSize;
            var f = Frame(false, 0);
            // the same random routes as in Rhino (circles, figure-eights, spirals, hearts, bounces ...) across the canvas
            double ax = Math.Max(20, c.Width / 2.0 - f.Width - 30), ay = Math.Max(20, c.Height / 2.0 - f.Height - 30);
            double cx = c.Width / 2.0 - f.Width / 2.0, cy = c.Height / 2.0 - f.Height / 2.0;
            var (nx, ny) = StatusDot.Norm(_path, _runT);
            double x = cx + ax * nx, y = cy + ay * ny;
            double bl = Math.Min(1, (now - _pathStart).TotalSeconds / 1.3);
            if (_prev != null && bl < 1)
            {
                var (px, py) = StatusDot.Norm(_prev, _runT);
                double e = bl * bl * (3 - 2 * bl);
                x = cx + ax * (px + (nx - px) * e); y = cy + ay * (py + (ny - py) * e);
            }
            if (Math.Abs(x - _lastX) > 0.3) _faceRight = x > _lastX;
            _lastX = x;
            var r = new Rectangle((int)x, (int)y, f.Width, f.Height);
            var old = _runRect;
            _runRect = r;
            if (!old.IsEmpty) _canvas.Invalidate(Inflate(old));
            _canvas.Invalidate(Inflate(r));
        }

        static void PaintRunner(System.Drawing.Graphics g)
        {
            if (_run == null || _runRect.IsEmpty || _canvas == null) return;
            var c = _canvas.ClientSize;
            // facing the way it runs; steps in time with its pace
            bool flip = _faceRight;
            int step = ((int)(_runT * 6)) % 2;
            g.DrawImage(Frame(flip, step), _runRect);
        }

        static void OnPaint(Grasshopper.GUI.Canvas.GH_Canvas sender)
        {
            var g = sender.Graphics;
            if (g == null) return;
            var savedR = g.Transform;
            g.ResetTransform();
            try { PaintRunner(g); } catch { }
            g.Transform = savedR;
            var saved = g.Transform;
            g.ResetTransform();
            var c = StateColor();
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
