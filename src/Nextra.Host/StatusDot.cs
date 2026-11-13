using System;
using System.IO;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Minimal movable status light, written in Eto so Windows and Mac share this one implementation.
    /// The dot is owned by Rhino's main window (RhinoEtoApp.MainWindow), so it floats over Rhino and never over other
    /// programs. While Rhino is the active app (also when the Grasshopper editor is in front) it is Topmost.
    /// green = an LLM bridge talked to us in the last 90 s, amber = server up but no client, red = server down.
    /// Follows the Rhino window (bottom-right corner + offset); drag to move (offset is remembered, in logical pixels).
    /// Click: red → restart the server (tries the next free port), then the status goes to the command line.
    class StatusDot : Form
    {
        const int D = 16;
        static StatusDot _instance;
        readonly UITimer _timer = new UITimer { Interval = 0.3 };
        readonly Drawable _canvas = new Drawable { Size = new Size(D, D) };
        Window _owner;
        Point _offset;          // from the owner window's bottom-right corner
        int _tick;
        Color _color = Colors.Gray;
        PointF _down;
        bool _pressed, _dragged;

        public static void Open()
        {
            if (_instance != null) return;
            _instance = new StatusDot();
            _instance.Show();
            _instance.KeepSize();                     // the size must be set again once the window exists (see KeepSize)
            PlatformQuirks.AfterShow(_instance);      // Windows: undo Eto.Wpf's blur-behind (WindowsQuirks.cs); Mac: nothing
        }

        /// Windows enlarges a new window to its minimum window size (about 133 x 38); setting the size after the window
        /// is shown sticks. Also re-applied while following, in case the system changes it again (e.g. a DPI change).
        void KeepSize()
        {
            if (Size.Width != D || Size.Height != D) Size = new Size(D, D);
        }

        public static void CloseDot()
        {
            try { _instance?.Close(); } catch { }
            _instance = null;
        }

        StatusDot()
        {
            WindowStyle = WindowStyle.None;
            Resizable = false;
            Minimizable = false;
            Maximizable = false;
            ShowInTaskbar = false;
            ShowActivated = false;          // never take the focus from Rhino's command line
            BackgroundColor = Colors.Transparent;
            ClientSize = new Size(D, D);
            _canvas.BackgroundColor = Colors.Transparent;
            _canvas.Paint += OnPaintDot;
            _canvas.MouseDown += OnDown;
            _canvas.MouseMove += OnMove;
            _canvas.MouseUp += OnUp;
            Content = _canvas;
            _offset = LoadOffset();
            AttachOwner();
            _timer.Elapsed += (s, e) => { Follow(); if (_tick++ % 3 == 0) Tick(); };
            _timer.Start();
            Tick();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            base.OnClosed(e);
        }

        /// Rhino's main window as Rhino reports it now (on Mac: the window of the active document).
        void AttachOwner()
        {
            Window w = null;
            try { w = Rhino.UI.RhinoEtoApp.MainWindow; } catch { }
            if (w == null || ReferenceEquals(w, _owner)) return;
            _owner = w;
            try { Owner = w; } catch { }
        }

        /// Stick to the Rhino window: bottom-right corner + remembered offset; hide while Rhino is minimised.
        void Follow()
        {
            AttachOwner();
            if (_owner == null || _pressed) return;
            if (_owner.WindowState == WindowState.Minimized) { if (Visible) Visible = false; return; }
            if (!Visible) Visible = true;
            KeepSize();
            bool active = Application.Instance.IsActive;     // Rhino (or its GH editor) is the app in front
            if (Topmost != active) Topmost = active;
            var p = OwnerBottomRight() + new Size(_offset.X, _offset.Y);
            if (p != Location) Location = p;
        }

        Point OwnerBottomRight()
        {
            var b = _owner?.Bounds ?? Rectangle.Empty;
            return new Point(b.X + b.Width, b.Y + b.Height);
        }

        /// Colour (ARGB) and text of the current state; shared with the Grasshopper canvas dot (GhDot.cs).
        public static void CurrentState(out int argb, out string text)
        {
            if (!Server.IsListening) { argb = Argb(220, 50, 50); text = "NextraRhinoGh: server down. Click to restart."; }
            else if ((DateTime.Now - Server.LastClientCall).TotalSeconds < 90) { argb = Argb(40, 190, 80); text = $"NextraRhinoGh: connected (127.0.0.1:{Server.Port})"; }
            else { argb = Argb(240, 170, 30); text = $"NextraRhinoGh: ready on 127.0.0.1:{Server.Port}, no LLM client yet"; }
            var busy = Dispatcher.BusyText();
            if (busy != null) text += "\nbusy: " + busy;
        }

        static int Argb(int r, int g, int b) => unchecked((int)0xFF000000) | (r << 16) | (g << 8) | b;

        void Tick()
        {
            CurrentState(out var argb, out _);
            var c = Color.FromArgb(argb);
            if (c != _color) { _color = c; _canvas.Invalidate(); }
        }

        void OnPaintDot(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.AntiAlias = true;
            g.FillEllipse(_color, 2, 2, 11, 11);
            using var pen = new Pen(Color.FromArgb(60, 60, 60), 1.5f);
            g.DrawEllipse(pen, 2, 2, 11, 11);
        }

        void OnDown(object sender, MouseEventArgs e)
        {
            if (e.Buttons != MouseButtons.Primary) return;
            _down = e.Location; _pressed = true; _dragged = false;
            e.Handled = true;
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed) return;
            var d = e.Location - _down;
            if (Math.Abs(d.X) + Math.Abs(d.Y) > 2) { _dragged = true; Location = new Point(Location.X + (int)d.X, Location.Y + (int)d.Y); }
            e.Handled = true;
        }

        void OnUp(object sender, MouseEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            e.Handled = true;
            if (_dragged)
            {
                var br = OwnerBottomRight();
                _offset = new Point(Location.X - br.X, Location.Y - br.Y);
                SaveOffset(_offset);
                return;
            }
            ClickAction();
            Tick();
        }

        public static void ClickAction()
        {
            if (!Server.IsListening)
            {
                try { Server.Stop(); Server.Start(); } catch (Exception ex) { RhinoApp.WriteLine("NextraRhinoGh: restart failed: " + ex.Message); }
            }
            RhinoApp.WriteLine($"NextraRhinoGh: {(Server.IsListening ? "listening on 127.0.0.1:" + Server.Port : "server down")}, engine {EngineLoader.Describe()}, last client call {(Server.LastClientCall == DateTime.MinValue ? "never" : Server.LastClientCall.ToString("HH:mm:ss"))}");
        }

        static string PosFile => Path.Combine(Server.AppDir, "dot.txt");

        // "eto,x,y" in logical pixels. Older files ("off,x,y", physical pixels of the WinForms dot) fall back to the default.
        // default: just inside the bottom-right corner of Rhino (near the status bar)
        static Point LoadOffset()
        {
            try { var t = File.ReadAllText(PosFile).Split(','); if (t.Length == 3 && t[0] == "eto") return new Point(int.Parse(t[1]), int.Parse(t[2])); } catch { }
            return new Point(-40, -30);
        }

        static void SaveOffset(Point p) { try { File.WriteAllText(PosFile, $"eto,{p.X},{p.Y}"); } catch { } }
    }
}
