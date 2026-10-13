using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Minimal movable status light, owned by the Rhino window (floats over Rhino and the GH editor).
    /// green = an LLM bridge talked to us in the last 90 s, amber = server up but no client, red = server down.
    /// Follows the Rhino window; drag to move (offset is remembered). Click: red → restart server (tries the next free port); otherwise shows status.
    class StatusDot : Form
    {
        static StatusDot _instance;
        readonly Timer _timer = new Timer { Interval = 300 };
        IntPtr _owner;
        Point _offset;     // from the owner window's bottom-right corner
        int _tick;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        Color _color = Color.Gray;
        Point _down;
        bool _dragged;
        readonly ToolTip _tip = new ToolTip();

        public static void Show(IntPtr owner)
        {
            if (_instance != null) return;
            _instance = new StatusDot { _owner = owner };
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
            AutoScaleMode = AutoScaleMode.None;          // no DPI auto-scaling of the 16 px dot
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = new Size(16, 16); MaximumSize = new Size(16, 16);
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(16, 16);
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            DoubleBuffered = true;
            _offset = LoadOffset();
            _timer.Tick += (s, e) => { Follow(); if (_tick++ % 3 == 0) Tick(); };
            _timer.Start();
            Tick();
        }

        protected override bool ShowWithoutActivation => true;

        /// Stick to the Rhino window: bottom-right corner + remembered offset; hide while Rhino is minimised.
        void Follow()
        {
            if (_owner == IntPtr.Zero || Capture) return;
            if (IsIconic(_owner)) { if (Visible) Hide(); return; }
            if (!Visible) Show();
            if (!GetWindowRect(_owner, out var r)) return;
            // above Rhino AND the Grasshopper editor while either is the active app; never above other programs
            GetWindowThreadProcessId(GetForegroundWindow(), out uint fg);
            bool ours = fg == (uint)Environment.ProcessId;
            if (TopMost != ours) TopMost = ours;
            var p = new Point(r.R + _offset.X, r.B + _offset.Y);
            if (p != Location) Location = p;
        }

        Point OwnerBottomRight() => GetWindowRect(_owner, out var r) ? new Point(r.R, r.B) : Point.Empty;

        public static void CurrentState(out Color c, out string t)
        {
            if (!Server.IsListening) { c = Color.FromArgb(220, 50, 50); t = "NextraRhinoGh: server down. Click to restart."; }
            else if ((DateTime.Now - Server.LastClientCall).TotalSeconds < 90) { c = Color.FromArgb(40, 190, 80); t = $"NextraRhinoGh: connected (127.0.0.1:{Server.Port})"; }
            else { c = Color.FromArgb(240, 170, 30); t = $"NextraRhinoGh: ready on 127.0.0.1:{Server.Port}, no LLM client yet"; }
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

        // default: just inside the bottom-right corner of Rhino (near the status bar)
        static Point LoadOffset()
        {
            try { var t = File.ReadAllText(PosFile).Split(','); if (t.Length == 3 && t[0] == "off") return new Point(int.Parse(t[1]), int.Parse(t[2])); } catch { }
            return new Point(-46, -34);
        }

        static void SaveOffset(Point p) { try { File.WriteAllText(PosFile, $"off,{p.X},{p.Y}"); } catch { } }
    }
}
