using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Minimal movable status light, owned by the Rhino window (floats over Rhino and the GH editor).
    /// green = an LLM bridge talked to us in the last 90 s, amber = server up but no client, red = server down.
    /// Drag to move (position is remembered). Click: red → restart server (tries the next free port); otherwise shows status.
    class StatusDot : Form
    {
        static StatusDot _instance;
        readonly Timer _timer = new Timer { Interval = 1000 };
        Color _color = Color.Gray;
        Point _down;
        bool _dragged;
        readonly ToolTip _tip = new ToolTip();

        public static void Show(IntPtr owner)
        {
            if (_instance != null) return;
            _instance = new StatusDot();
            var nw = new NativeWindow();
            try { nw.AssignHandle(owner); } catch { }
            _instance.Show(nw);
        }

        public static void CloseDot()
        {
            try { _instance?.Close(); } catch { }
            _instance = null;
        }

        StatusDot()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(16, 16);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            Location = LoadPos();
            _timer.Tick += (s, e) => Tick();
            _timer.Start();
            Tick();
        }

        protected override bool ShowWithoutActivation => true;

        public static void CurrentState(out Color c, out string t)
        {
            if (!Server.IsListening) { c = Color.FromArgb(220, 50, 50); t = "Nextra: server down. Click to restart."; }
            else if ((DateTime.Now - Server.LastClientCall).TotalSeconds < 90) { c = Color.FromArgb(40, 190, 80); t = $"Nextra: connected (127.0.0.1:{Server.Port})"; }
            else { c = Color.FromArgb(240, 170, 30); t = $"Nextra: ready on 127.0.0.1:{Server.Port}, no LLM client yet"; }
            var busy = Dispatcher.BusyText();
            if (busy != null) t += "\nbusy: " + busy;
        }

        void Tick()
        {
            CurrentState(out var c, out var t);
            if (c != _color) { _color = c; Invalidate(); }
            _tip.SetToolTip(this, t);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(_color);
            using var p = new Pen(Color.FromArgb(60, 60, 60), 1.5f);
            g.FillEllipse(b, 2, 2, 11, 11);
            g.DrawEllipse(p, 2, 2, 11, 11);
        }

        protected override void OnMouseDown(MouseEventArgs e) { _down = e.Location; _dragged = false; }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            var d = new Size(e.X - _down.X, e.Y - _down.Y);
            if (Math.Abs(d.Width) + Math.Abs(d.Height) > 2) { _dragged = true; Location += d; }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_dragged) { SavePos(Location); return; }
            ClickAction();
            Tick();
        }

        public static void ClickAction()
        {
            if (!Server.IsListening)
            {
                try { Server.Stop(); Server.Start(); } catch (Exception ex) { RhinoApp.WriteLine("Nextra: restart failed: " + ex.Message); }
            }
            RhinoApp.WriteLine($"Nextra: {(Server.IsListening ? "listening on 127.0.0.1:" + Server.Port : "server down")}, engine {EngineLoader.Describe()}, last client call {(Server.LastClientCall == DateTime.MinValue ? "never" : Server.LastClientCall.ToString("HH:mm:ss"))}");
        }

        static string PosFile => Path.Combine(Server.AppDir, "dot.txt");

        static Point LoadPos()
        {
            try
            {
                var s = File.ReadAllText(PosFile).Split(',');
                var p = new Point(int.Parse(s[0]), int.Parse(s[1]));
                foreach (var scr in Screen.AllScreens) if (scr.WorkingArea.Contains(p)) return p;
            }
            catch { }
            var wa = Screen.PrimaryScreen.WorkingArea;
            return new Point(wa.Right - 40, wa.Bottom - 40);
        }

        static void SavePos(Point p) { try { File.WriteAllText(PosFile, $"{p.X},{p.Y}"); } catch { } }
    }
}
