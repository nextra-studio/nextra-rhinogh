using System;
using System.IO;
using Eto.Drawing;
using Eto.Forms;
using Rhino;

namespace Nextra.Host
{
    /// Nexiy, the movable status figure in the Rhino window (it replaced the status dot; the Grasshopper canvas keeps the dot
    /// and gets a runner at Chaos, GhDot.cs). Written in Eto so Windows and Mac share this one implementation.
    /// The window is owned by Rhino's main window, so it floats over Rhino and never over other programs; Topmost only while
    /// Rhino is in front; never takes the focus.
    /// Body colour: lime = an AI client used Rhino in the last 90 s, amber = server up but no client, red (worried) = down.
    /// Liveliness (nexiy_level, set in the click menu or the assistant's settings):
    ///   0 Calm     same size, never moves: a blink, a hop when a client connects, a shake when the server goes down
    ///   1 Lively   + the nextra.workspace acts in place: looks around, stretches, eats, drinks coffee, jumps, flips, waves;
    ///              acts out the running tool (laptop, glasses, coffee), cheers (confetti) or cries at the result
    ///   2 Playful  + while something runs (ChatGPT / Claude through the bridge, or the assistant): grows and dances along
    ///              curved paths near its corner, then walks back home
    ///   3 Chaos    + bigger, laps across the whole Rhino window (figure-eights, spins), and runs around the Grasshopper canvas
    /// Moving only while something runs; clicks pass through it while it roams. No hover text (by design).
    /// Drag to move its home (remembered). Click: status, size, liveliness, hide.
    class StatusDot : Form
    {
        public static readonly int[] Sizes = { 16, 24, 40, 64 };
        public static readonly string[] SizeNames = { "Tiny", "Small", "Medium", "Large" };
        public static readonly string[] LevelNames = { "Calm", "Lively", "Playful", "Chaos" };
        const int DefaultSize = 24, DefaultLevel = 1;   // Lively by default: little acts in place, never roams or grows

        enum Link { Down, Ready, Connected }
        enum Act { None, Blink, Hop, Shake, Look, Stretch, Eat, Drink, Jump, Flip, Wave, Party, Cry, Moves }

        static StatusDot _instance;
        readonly UITimer _timer = new UITimer { Interval = 0.3 };
        readonly UITimer _anim = new UITimer { Interval = 0.04 };      // ~25 fps, only while something moves
        readonly Drawable _canvas = new Drawable();
        readonly Random _rnd = new Random();
        Window _owner;
        Point _offset;          // home: window top-left from the owner window's bottom-right corner (at the home size)
        int _size, _shown;      // chosen size, size drawn now
        int _level;
        bool _hidden;
        int _tick;
        PointF _down;
        bool _pressed, _dragged;

        // state
        Link _link = Link.Ready;
        bool _first = true, _busy, _wasActive, _armUp, _dotsLit;
        string _tool;
        Act _act;
        DateTime _actStart, _nextBlink = DateTime.Now.AddSeconds(4), _nextIdle = DateTime.Now.AddSeconds(20), _lookUntil;
        DateTime _sparksUntil, _lastActivity = DateTime.Now, _lastEndSeen = DateTime.MinValue;
        // roaming
        bool _roaming, _clickThrough;
        double _roamT, _blend;  // path time; 0 = at home, 1 = on the path
        DateTime _lastFrame = DateTime.Now, _nextSpin = DateTime.Now.AddSeconds(5), _spinStart = DateTime.MinValue;
        PointF _lastPos;
        bool _faceRight;

        /// Set by the assistant panel while it waits for a model (purple, thinking dots).
        public static bool Thinking { get; set; }

        /// working: a tool call running (any client), one that ended in the last 8 s (an AI session calls every few seconds and
        /// thinks in between; tool calls block Rhino's UI thread, so Nexiy moves in those gaps), or the assistant thinking
        public static bool Active => Dispatcher.BusyTool() != null || Thinking || (DateTime.Now - Dispatcher.LastEnd).TotalSeconds < 8;
        public static int Level => _instance?._level ?? Config.GetInt("nexiy_level", DefaultLevel);

        /// The assistant finished a change: a hop with sparks (party when lively). Failed: a shake (cry when lively).
        public static void Celebrate() { if (_instance == null) return; _instance._sparksUntil = DateTime.Now.AddSeconds(2); _instance._lastActivity = DateTime.Now; _instance.Play(_instance._level >= 1 ? Act.Party : Act.Hop); }
        public static void Fail() { if (_instance == null) return; _instance._lastActivity = DateTime.Now; _instance.Play(_instance._level >= 1 ? Act.Cry : Act.Shake); }

        public static void Open()
        {
            if (_instance != null) return;
            _instance = new StatusDot();
            _instance.Show();
            _instance.KeepSize();                     // the size must be set again once the window exists (see KeepSize)
            PlatformQuirks.AfterShow(_instance);      // Windows: undo Eto.Wpf's blur-behind (WindowsQuirks.cs); Mac: nothing
            if (_instance._hidden) _instance.Visible = false;
        }

        public static void CloseDot()
        {
            try { _instance?.Close(); } catch { }
            _instance = null;
        }

        /// Show or hide Nexiy (remembered across Rhino restarts).
        public static void SetHidden(bool hidden)
        {
            Config.Set("nexiy_hidden", hidden);
            if (_instance == null) return;
            _instance._hidden = hidden;
            if (hidden) _instance.Visible = false;
            else { _instance.Visible = true; _instance.Follow(); _instance.Play(Act.Hop); }
        }

        /// Figure width in pixels (16 tiny … 64 large); remembered. Keeps the figure's bottom-right corner in place.
        public static void SetSize(int size)
        {
            size = Math.Max(12, Math.Min(128, size));
            Config.Set("nexiy_size", size);
            if (_instance == null) return;
            var old = NexiyArt.WindowSize(_instance._size);
            var now = NexiyArt.WindowSize(size);
            _instance._size = size;
            _instance._offset = new Point(_instance._offset.X - (now.Width - old.Width), _instance._offset.Y - (now.Height - old.Height));
            SaveOffset(_instance._offset);
            _instance.Resize(size);
            _instance.Follow();
        }

        /// How lively Nexiy is: 0 Calm, 1 Lively, 2 Playful, 3 Chaos (remembered).
        public static void SetLevel(int level)
        {
            level = Math.Max(0, Math.Min(3, level));
            Config.Set("nexiy_level", level);
            if (_instance == null) return;
            _instance._level = level;
            _instance._nextIdle = DateTime.Now.AddSeconds(2);
            if (_instance._roaming) _instance.Resize(_instance.RoamSize());   // a new level while roaming: its size at once
            _instance.UpdateRoaming(Active);
            _instance.Play(level == 0 ? Act.Blink : Act.Jump);
        }

        /// Nexiy's window on screen (the assistant panel opens out of it and closes back into it), or null when it is hidden
        public static Rectangle? ScreenBounds => _instance != null && !_instance._hidden && _instance.Visible ? new Rectangle(_instance.Location, _instance.Size) : (Rectangle?)null;

        public static int CurrentSize => _instance?._size ?? Config.GetInt("nexiy_size", DefaultSize);
        public static bool IsHidden => _instance?._hidden ?? Config.GetBool("nexiy_hidden", false);

        /// Windows enlarges a new window to its minimum window size (about 133 x 38); setting the size after the window
        /// is shown sticks. Also re-applied while following, in case the system changes it again (e.g. a DPI change).
        void KeepSize()
        {
            var want = NexiyArt.WindowSize(_shown);
            if (Size != want) Size = want;
        }

        void Resize(int shown)
        {
            _shown = shown;
            var ws = NexiyArt.WindowSize(shown);
            _canvas.Size = ws;
            ClientSize = ws;
            KeepSize();
            _canvas.Invalidate();
        }

        StatusDot()
        {
            _size = _shown = Config.GetInt("nexiy_size", DefaultSize);
            _level = Math.Max(0, Math.Min(3, Config.GetInt("nexiy_level", DefaultLevel)));
            _hidden = Config.GetBool("nexiy_hidden", false);
            WindowStyle = WindowStyle.None;
            Resizable = false;
            Minimizable = false;
            Maximizable = false;
            ShowInTaskbar = false;
            ShowActivated = false;          // never take the focus from Rhino's command line
            BackgroundColor = Colors.Transparent;
            ClientSize = NexiyArt.WindowSize(_size);
            _canvas.Size = ClientSize;
            _canvas.BackgroundColor = Colors.Transparent;
            _canvas.Paint += OnPaintFigure;
            _canvas.MouseDown += OnDown;
            _canvas.MouseMove += OnMove;
            _canvas.MouseUp += OnUp;
            Content = _canvas;
            _offset = LoadOffset(_size);
            AttachOwner();
            _timer.Elapsed += (s, e) => { Follow(); if (_tick++ % 3 == 0) Tick(); else Idle(); };
            _anim.Elapsed += (s, e) => AnimFrame();
            _timer.Start();
            Tick();
        }

        protected override void OnClosed(EventArgs e)
        {
            _timer.Stop();
            _anim.Stop();
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

        /// Stick to the Rhino window: bottom-right corner + remembered offset; hide while Rhino is minimised. While roaming,
        /// the animation places the window instead.
        void Follow()
        {
            AttachOwner();
            if (_owner == null || _pressed) return;
            if (_hidden || _owner.WindowState == WindowState.Minimized) { if (Visible) Visible = false; return; }
            if (!Visible) Visible = true;
            KeepSize();
            bool active = Application.Instance.IsActive;     // Rhino (or its GH editor) is the app in front
            if (Topmost != active) Topmost = active;
            if (_roaming || _blend > 0) return;
            var p = Home();
            if (p != Location) Location = p;
        }

        /// home: the bottom-right corner of the figure stays where the person put it, whatever size is drawn now
        Point Home()
        {
            var b = _owner?.Bounds ?? Rectangle.Empty;
            var baseSize = NexiyArt.WindowSize(_size);
            var now = NexiyArt.WindowSize(_shown);
            return new Point(b.X + b.Width + _offset.X - (now.Width - baseSize.Width), b.Y + b.Height + _offset.Y - (now.Height - baseSize.Height));
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

        static Link CurrentLink() =>
            !Server.IsListening ? Link.Down
            : (DateTime.Now - Server.LastClientCall).TotalSeconds < 90 ? Link.Connected
            : Link.Ready;

        /// Every 0.9 s: state changes and the reactions to them.
        void Tick()
        {
            var link = CurrentLink();
            var running = Dispatcher.BusyTool();
            if (running != null) _tool = running;
            bool active = Active;
            bool busy = active && !Thinking;          // working for an AI client (acting out _tool, the last tool it used)
            if (active || link == Link.Connected) _lastActivity = DateTime.Now;
            if (!_first && link != _link)
            {
                if (link == Link.Down) Play(_level >= 1 ? Act.Cry : Act.Shake);
                else if (link == Link.Connected) { Play(_level >= 1 ? Act.Jump : Act.Hop); _sparksUntil = DateTime.Now.AddSeconds(2.5); }
                else if (_link == Link.Down) Play(Act.Hop);
            }
            // a burst of work (ChatGPT / Claude through the bridge, or the assistant) just ended: cheer or cry once
            if (!active && _wasActive && Dispatcher.LastEnd > _lastEndSeen)
            {
                _lastEndSeen = Dispatcher.LastEnd;
                if (Dispatcher.LastOk) { _sparksUntil = DateTime.Now.AddSeconds(1.5); Play(_level >= 1 ? Act.Party : Act.Hop); }
                else Play(_level >= 1 ? Act.Cry : Act.Shake);
            }
            _first = false;
            bool changed = link != _link || busy != _busy || active != _wasActive;
            _link = link;
            _busy = busy;
            _wasActive = active;
            UpdateRoaming(active);
            if (changed) _canvas.Invalidate();
        }

        /// Playful / Chaos: grow and roam while something runs; walk back and shrink when it stops. Calm / Lively: never.
        void UpdateRoaming(bool active)
        {
            bool want = active && _level >= 2 && !_hidden && Visible && _owner != null && _owner.WindowState != WindowState.Minimized;
            if (want == _roaming) return;
            _roaming = want;
            if (want)
            {
                Resize(RoamSize());
                _roamT = 0;
                _lastPos = new PointF(Location.X, Location.Y);
                SetClickThrough(true);
            }
            StartAnim();
        }

        int RoamSize() => _level >= 3 ? Math.Max(_size, 64) : Math.Max(_size, Math.Min(56, _size * 2));

        void SetClickThrough(bool on)
        {
            if (on == _clickThrough) return;
            _clickThrough = on;
            try { PlatformQuirks.ClickThrough(this, on); } catch { }
        }

        /// The two ticks in between: working arm, thinking dots, blinks, glances and the idle acts.
        void Idle()
        {
            if (_hidden || !Visible) return;
            bool repaint = false;
            if (_busy && _level == 0) { _armUp = !_armUp; repaint = true; }
            else if (_armUp) { _armUp = false; repaint = true; }
            if (Thinking) { _dotsLit = !_dotsLit; repaint = true; }
            if (_lookUntil != DateTime.MinValue && DateTime.Now > _lookUntil) { _lookUntil = DateTime.MinValue; repaint = true; }
            if (DateTime.Now < _sparksUntil.AddSeconds(0.6)) repaint = true;
            var now = DateTime.Now;
            bool calmMoment = _act == Act.None && !_roaming && !_busy && !Thinking && Application.Instance.IsActive && !Asleep && _link != Link.Down;
            if (calmMoment)
            {
                if (now > _nextBlink) { _nextBlink = now.AddSeconds(3 + _rnd.NextDouble() * 4); Play(Act.Blink); }
                else if (_level >= 1 && now > _nextIdle)
                {
                    double gap = _level == 1 ? 18 + _rnd.NextDouble() * 18 : _level == 2 ? 10 + _rnd.NextDouble() * 10 : 5 + _rnd.NextDouble() * 6;
                    _nextIdle = now.AddSeconds(gap);
                    _moves = NewMoves(_level, false);
                    Play(Act.Moves);
                }
            }
            // acting out the running tool in place (Lively), or while roaming (Playful / Chaos): keep the frames coming
            if ((_busy || Thinking) && _level >= 1) StartAnim();
            if (repaint) _canvas.Invalidate();
        }

        bool Asleep => _link == Link.Ready && !_busy && !Thinking && (DateTime.Now - _lastActivity).TotalSeconds > 120;

        void Play(Act act)
        {
            if (_hidden) return;
            _act = act;
            _actStart = DateTime.Now;
            if (act == Act.Look) _lookUntil = DateTime.Now.AddSeconds(2.4);
            StartAnim();
        }

        void StartAnim() { if (!_anim.Started) { _lastFrame = DateTime.Now; _anim.Start(); } }

        double ActLength => _act switch
        {
            Act.Blink => 0.14, Act.Hop => 0.5, Act.Shake => 0.6, Act.Look => 2.4, Act.Stretch => 1.8, Act.Eat => 2.6, Act.Drink => 2.8,
            Act.Jump => 0.9, Act.Flip => 0.8, Act.Wave => 1.6, Act.Party => 2.0, Act.Cry => 2.4, Act.Moves => _moves?.Len ?? 2, _ => 0,
        };

        void AnimFrame()
        {
            var now = DateTime.Now;
            double dt = Math.Min(0.1, (now - _lastFrame).TotalSeconds);
            _lastFrame = now;
            if (_act != Act.None && (now - _actStart).TotalSeconds > ActLength) _act = Act.None;

            // roaming: blend in / out between home and the path
            _blend = Math.Max(0, Math.Min(1, _blend + (_roaming ? dt : -dt) / 0.7));
            if (_blend > 0 && _owner != null)
            {
                _roamT += dt * SpeedNow;
                if (_path == null || now > _pathUntil) NextPath(now);
                var home = Home();
                var path = PathPoint(_roamT);
                double e = _blend * _blend * (3 - 2 * _blend);     // smooth start and stop
                var x = home.X + (path.X - home.X) * e;
                var y = home.Y + (path.Y - home.Y) * e;
                if (Math.Abs(x - _lastPos.X) > 0.5) _faceRight = x > _lastPos.X;
                _lastPos = new PointF((float)x, (float)y);
                var p = new Point((int)Math.Round(x), (int)Math.Round(y));
                if (p != Location) Location = p;
                if (_level >= 3 && now > _nextSpin) { _spinStart = now; _nextSpin = now.AddSeconds(8 + _rnd.NextDouble() * 8); }
            }
            else if (!_roaming && _shown != _size)
            {
                Resize(_size);                  // back home: back to the chosen size
                SetClickThrough(false);
                Follow();
            }
            if (_blend <= 0 && !_roaming) SetClickThrough(false);

            bool more = _act != Act.None || _roaming || _blend > 0 || ((_busy || Thinking) && _level >= 1);
            if (!more) _anim.Stop();
            _canvas.Invalidate();
        }

        // ---- variety: path families and moves, combined at random (hundreds of variations; rarely the same twice) ---------
        internal sealed class RoamPath { public int Kind; public double Speed, Phase, Dir, Rot, Sx, Sy, A, B, C; }
        sealed class Moves { public NexiyProp Prop; public int Arms, Legs, Body, Face; public double Tempo, Len; }
        RoamPath _path, _prevPath;
        DateTime _pathStart, _pathUntil;
        Moves _moves, _dance;
        static readonly (double, double)[] Ratios = { (1, 2), (2, 3), (3, 4), (3, 5), (1, 3), (2, 5), (4, 5) };

        void NextPath(DateTime now)
        {
            _prevPath = _path;
            var r = _rnd;
            _path = RandomPath(r);
            _pathStart = now;
            _pathUntil = now.AddSeconds(7 + r.NextDouble() * 7);
            _dance = NewMoves(_level, true);
        }

        /// a random route: one of 13 families with its own size, pace, direction, rotation and stretch (calm paces: 0.12..0.3)
        internal static RoamPath RandomPath(Random r) => new RoamPath
        {
            Kind = r.Next(13), Speed = 0.12 + r.NextDouble() * 0.18, Phase = r.NextDouble() * Math.PI * 2, Dir = r.Next(2) == 0 ? 1 : -1,
            Rot = r.NextDouble() * Math.PI, Sx = 0.6 + r.NextDouble() * 0.4, Sy = 0.6 + r.NextDouble() * 0.4,
            A = 2 + r.Next(4), B = r.NextDouble(), C = r.NextDouble() * 3,
        };

        /// how fast Nexiy roams: 0.6 slow, 1 normal (default), 1.4 lively — never faster (it is a companion, not a toy)
        public static readonly double[] Speeds = { 0.6, 1.0, 1.4 };
        public static readonly string[] SpeedNames = { "Slow", "Normal", "Lively" };
        public static double Speed => Speeds[Math.Max(0, Math.Min(2, Config.GetInt("nexiy_speed", 1)))];
        public static int SpeedIndex => Math.Max(0, Math.Min(2, Config.GetInt("nexiy_speed", 1)));
        public static void SetSpeed(int i) { Config.Set("nexiy_speed", Math.Max(0, Math.Min(2, i))); _speedCache = Speed; }
        static double _speedCache = -1;
        static double SpeedNow => _speedCache < 0 ? (_speedCache = Speed) : _speedCache;

        static double Tri(double x) { x = x - Math.Floor(x); return x < 0.5 ? 4 * x - 1 : 3 - 4 * x; }     // -1..1 triangle wave

        /// one path family at time t, in -1..1 (before rotation and stretch)
        internal static (double, double) Norm(RoamPath p, double t)
        {
            double u = t * p.Speed * p.Dir * 2 + p.Phase;
            double x, y;
            switch (p.Kind)
            {
                case 0: x = Math.Cos(u); y = Math.Sin(u); break;                                                     // circle
                case 1: x = Math.Cos(u); y = 0.5 * Math.Sin(u); break;                                               // ellipse
                case 2: x = Math.Sin(u); y = 0.6 * Math.Sin(2 * u); break;                                           // figure-eight
                case 3: { var (a, b) = Ratios[(int)(p.C * 2.3) % Ratios.Length]; x = Math.Sin(a * u * .6 + p.Phase); y = Math.Sin(b * u * .6); break; }   // Lissajous
                case 4: { double rr = 0.2 + 0.8 * (0.5 + 0.5 * Math.Sin(u * 0.25)); x = rr * Math.Cos(u * 1.5); y = rr * Math.Sin(u * 1.5); break; }    // spiral in and out
                case 5: { double rr = Math.Cos(p.A * u * .5); x = rr * Math.Cos(u * .5); y = rr * Math.Sin(u * .5); break; }                            // rose
                case 6: { double v = u * .7; x = Math.Pow(Math.Sin(v), 3); y = -(13 * Math.Cos(v) - 5 * Math.Cos(2 * v) - 2 * Math.Cos(3 * v) - Math.Cos(4 * v)) / 17; break; }   // heart
                case 7: { double v = (u * .25) % 4; if (v < 0) v += 4; int side = (int)v; double f = v - side * 1.0; x = side == 0 ? -1 + 2 * f : side == 1 ? 1 : side == 2 ? 1 - 2 * f : -1; y = side == 0 ? -1 : side == 1 ? -1 + 2 * f : side == 2 ? 1 : 1 - 2 * f; break; }   // square laps
                case 8: x = Tri(u * 0.08); y = 0.45 * Tri(u * 0.6); break;                                          // zigzag
                case 9: x = Tri(u * 0.061 + p.B); y = Tri(u * 0.089 + p.C); break;                                 // bouncing off the walls
                case 10: x = 0.65 * Math.Sin(u * (0.5 + p.B)) + 0.35 * Math.Sin(u * 1.7 + p.C); y = 0.6 * Math.Cos(u * (0.4 + p.B * .5)) + 0.4 * Math.Sin(u * 1.3); break;   // wander
                case 11: x = Tri(u * 0.06); y = 0.35 * Math.Sin(u * 2.2); break;                                   // waves
                default: { double rr = 0.65 + 0.35 * Math.Cos(5 * u * .5); x = rr * Math.Cos(u * .5); y = rr * Math.Sin(u * .5); break; }              // star
            }
            double c = Math.Cos(p.Rot), sn = Math.Sin(p.Rot);
            x *= p.Sx; y *= p.Sy;
            return (Math.Max(-1, Math.Min(1, x * c - y * sn)), Math.Max(-1, Math.Min(1, x * sn + y * c)));
        }

        /// where the figure goes (screen pixels, window top-left): the current path, blended from the last one
        PointF PathPoint(double t)
        {
            var b = _owner.Bounds;
            var ws = NexiyArt.WindowSize(_shown);
            double cx, cy, ax, ay;
            if (_level >= 3)
            {
                cx = b.X + b.Width / 2.0 - ws.Width / 2.0; cy = b.Y + b.Height / 2.0 - ws.Height / 2.0;
                ax = Math.Max(60, b.Width / 2.0 - ws.Width - 30); ay = Math.Max(40, b.Height / 2.0 - ws.Height - 50);
            }
            else
            {
                var h = Home();
                double r = 70 + _shown * 1.5;
                cx = h.X - r; cy = h.Y - r * 0.55; ax = r; ay = r * 0.55;
            }
            PointF At(RoamPath p) { var (x, y) = Norm(p, t); return new PointF((float)(cx + ax * x), (float)(cy + ay * y)); }
            var pt = At(_path);
            double bl = Math.Min(1, (DateTime.Now - _pathStart).TotalSeconds / 1.3);
            if (_prevPath != null && bl < 1)
            {
                var q = At(_prevPath);
                double e = bl * bl * (3 - 2 * bl);
                pt = new PointF((float)(q.X + (pt.X - q.X) * e), (float)(q.Y + (pt.Y - q.Y) * e));
            }
            float px = Math.Max(b.X + 6, Math.Min(b.X + b.Width - ws.Width - 6, pt.X));
            float py = Math.Max(b.Y + 50, Math.Min(b.Y + b.Height - ws.Height - 6, pt.Y));
            return new PointF(px, py);
        }

        /// a random routine: a prop, what the arms, legs, body and face do, a tempo and a length. Lively: the gentle ones.
        Moves NewMoves(int level, bool dancing)
        {
            var r = _rnd;
            bool wild = level >= 2;
            var props = new[] { NexiyProp.None, NexiyProp.None, NexiyProp.None, NexiyProp.Cup, NexiyProp.Snack, NexiyProp.Glasses, NexiyProp.Sunglasses, NexiyProp.Laptop, NexiyProp.Confetti };
            var m = new Moves
            {
                Prop = props[r.Next(wild ? props.Length : props.Length - 2)],
                Arms = r.Next(7),                          // 0 still, 1 both up, 2 alternate, 3 wave right, 4 wave left, 5 flap, 6 one up
                Legs = dancing ? 1 + r.Next(3) : r.Next(4), // 0 still, 1 march, 2 tap, 3 hop
                Body = wild ? r.Next(9) : new[] { 0, 1, 6, 7 }[r.Next(4)],   // 0 still, 1 bob, 2 jump, 3 spin, 4 flip, 5 shake, 6 sway, 7 tilt, 8 squat
                Face = r.Next(4),                          // 0 normal, 1 look around, 2 blinks, 3 eat (with the snack)
                Tempo = 0.45 + r.NextDouble() * 0.5,
                Len = 1.6 + r.NextDouble() * 2.4,
            };
            if (m.Prop == NexiyProp.Snack) m.Face = 3; else if (m.Face == 3) m.Face = 0;
            if (m.Prop == NexiyProp.Laptop) m.Arms = 2;
            return m;
        }

        /// apply a routine at time `sec` (seconds into it)
        static void Apply(NexiyLook k, Moves m, double sec, double len)
        {
            double w = sec * m.Tempo * Math.PI * 2;
            double t = len > 0 ? Math.Min(1, sec / len) : 0;
            if (m.Prop != NexiyProp.None) k.Prop = m.Prop;
            k.Phase = sec;
            switch (m.Arms)
            {
                case 1: k.ArmL = k.ArmR = 13; break;
                case 2: k.ArmL = (float)(Math.Max(0, Math.Sin(w * 2)) * 10); k.ArmR = (float)(Math.Max(0, -Math.Sin(w * 2)) * 10); break;
                case 3: k.ArmR = (float)(10 + 4 * Math.Sin(w * 3)); break;
                case 4: k.ArmL = (float)(10 + 4 * Math.Sin(w * 3)); break;
                case 5: k.ArmL = k.ArmR = (float)(Math.Abs(Math.Sin(w * 2)) * 12); break;
                case 6: k.ArmR = 13; break;
            }
            switch (m.Legs)
            {
                case 1: k.LegL = (float)(Math.Max(0, Math.Sin(w * 2)) * 4); k.LegR = (float)(Math.Max(0, -Math.Sin(w * 2)) * 4); break;
                case 2: k.LegR = (float)(Math.Max(0, Math.Sin(w * 3)) * 3); break;
                case 3: k.LegL = k.LegR = (float)(Math.Abs(Math.Sin(w)) * 3); break;
            }
            switch (m.Body)
            {
                case 1: k.Dy = -(float)Math.Abs(Math.Sin(w * 2)) * 4; break;
                case 2: k.Dy = -(float)Math.Abs(Math.Sin(w)) * 14; break;
                case 3: k.Spin = (float)(360 * t); k.Dy = -(float)(Math.Sin(Math.PI * t) * 8); break;
                case 4: k.Spin = -(float)(360 * Math.Min(1, t * 1.6)); k.Dy = -(float)(Math.Sin(Math.PI * Math.Min(1, t * 1.6)) * 16); break;
                case 5: k.Dx = (float)(2.5 * Math.Sin(w * 6)); break;
                case 6: k.Dx = (float)(5 * Math.Sin(w)); break;
                case 7: k.Spin = (float)(14 * Math.Sin(w)); break;
                case 8: k.Dy = (float)(Math.Abs(Math.Sin(w * 2)) * 4); break;
            }
            switch (m.Face)
            {
                case 1: k.EyesDx = (float)(3 * Math.Sign(Math.Sin(w * .7))); k.Flip = Math.Sin(w * .35) < 0; break;
                case 2: if ((sec * 2.2) % 1 < .12) k.Face = NexiyFace.Blink; break;
                case 3: k.Face = NexiyFace.Eat; break;
            }
        }

        NexiyLook Look()
        {
            var k = new NexiyLook();
            var now = DateTime.Now;
            k.Body = Thinking ? NexiyArt.Purple : _link == Link.Down ? NexiyArt.Red : _link == Link.Connected ? NexiyArt.Lime : NexiyArt.Amber;
            k.Face = _link == Link.Down ? NexiyFace.Worried : Asleep ? NexiyFace.Asleep : NexiyFace.Normal;
            k.Zzz = k.Face == NexiyFace.Asleep;
            k.ArmR = _busy && _armUp ? 12 : 0;
            k.ThinkDots = Thinking;
            k.DotsLit = _dotsLit;
            k.Sparks = now < _sparksUntil;
            if (_lookUntil != DateTime.MinValue && now < _lookUntil) k.EyesDx = 3;
            double sec = (now - _actStart).TotalSeconds;
            double t = Math.Min(1, sec / Math.Max(0.01, ActLength));
            k.Phase = sec;

            // acting out what runs (Lively and up): the tool decides the prop
            if ((_busy || Thinking) && _level >= 1 && _act == Act.None)
            {
                double w = (now - DateTime.Today).TotalSeconds;
                k.Phase = w;
                if (Thinking && !_busy) k.Prop = NexiyProp.Cup;
                else if (_tool == "query" || _tool == "view" || _tool == "memory") k.Prop = NexiyProp.Glasses;
                else if (!_roaming || _level == 2) k.Prop = NexiyProp.Laptop;
                if (k.Prop == NexiyProp.Laptop) { k.ArmL = (float)(Math.Max(0, Math.Sin(w * 18)) * 3); k.ArmR = (float)(Math.Max(0, -Math.Sin(w * 18)) * 3); }
                if ((_roaming || _blend > 0) && _dance != null)
                {
                    // dancing while it goes: this path's random routine, facing the way it moves, a spin now and then
                    var keepProp = k.Prop;
                    double ds = (now - _pathStart).TotalSeconds;
                    Apply(k, _dance, ds, 0);
                    if (_level == 2 && keepProp != NexiyProp.None) k.Prop = keepProp;    // Playful keeps the tool's prop
                    if (_dance.Face != 1) k.Flip = _faceRight;
                    double sp = (now - _spinStart).TotalSeconds;
                    if (_level >= 3 && sp < 0.6) k.Spin = (float)(360 * sp / 0.6);
                }
            }

            switch (_act)
            {
                case Act.Blink: if (k.Face == NexiyFace.Normal) k.Face = NexiyFace.Blink; break;
                case Act.Hop: k.Dy = (float)(-8 * Math.Sin(Math.PI * t)); break;
                case Act.Shake: k.Dx = (float)(3 * Math.Sin(t * Math.PI * 6)); break;
                case Act.Look: k.EyesDx = sec % 1.6 < 0.8 ? 3 : -3; k.Flip = sec % 1.6 >= 0.8; break;
                case Act.Stretch: k.ArmL = k.ArmR = (float)(Math.Sin(Math.PI * t) * 14); k.Dy = -(float)(Math.Sin(Math.PI * t) * 2); break;
                case Act.Eat: k.Prop = NexiyProp.Snack; k.Face = NexiyFace.Eat; k.Dy = -(float)Math.Abs(Math.Sin(sec * 9)) * 1.5f; break;
                case Act.Drink: k.Prop = NexiyProp.Cup; k.Phase = sec / 1.4; break;
                case Act.Jump: k.Dy = (float)(-14 * Math.Abs(Math.Sin(Math.PI * t * 2))); k.ArmL = k.ArmR = 8; break;
                case Act.Flip: k.Dy = (float)(-14 * Math.Sin(Math.PI * t)); k.Spin = (float)(360 * t); break;
                case Act.Wave: k.ArmR = 14; k.Dy = 0; k.EyesDx = 2; k.Phase = sec; if ((sec * 4) % 1 < .5) k.ArmR = 10; break;
                case Act.Party: k.Prop = NexiyProp.Confetti; k.Dy = (float)(-12 * Math.Abs(Math.Sin(Math.PI * t * 3))); k.ArmL = k.ArmR = 12; k.Phase = sec; break;
                case Act.Cry: k.Face = NexiyFace.Cry; k.Dx = (float)(1.5 * Math.Sin(sec * 30)); k.Phase = sec; break;
                case Act.Moves: if (_moves != null) Apply(k, _moves, sec, ActLength); break;
            }
            return k;
        }

        void OnPaintFigure(object sender, PaintEventArgs e) => NexiyArt.Draw(e.Graphics, _shown, Look());

        void OnDown(object sender, MouseEventArgs e)
        {
            if (_roaming) return;
            if (e.Buttons == MouseButtons.Alternate) { e.Handled = true; ShowMenu(); return; }
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
            _lastActivity = DateTime.Now;
            // one click opens / closes the panel: the chat once the assistant is set up, before that (and in desktop-app mode)
            // the panel shows only the settings (Nexiy's liveliness and speed, the connection). Server down: the menu.
            if (CurrentLink() != Link.Down) AssistantPanel.Toggle();
            else ShowMenu();
        }

        /// Right click (or a left click before the assistant is set up): the assistant, connection status, size,
        /// liveliness, speed, hide.
        void ShowMenu()
        {
            var menu = new ContextMenu();
            {     // always there: the panel itself walks the person through the setup (API key, CLI or desktop app)
                bool setUp = false;
                try { setUp = AssistantSettings.SetUp; } catch { }
                var open = new ButtonMenuItem { Text = AssistantPanel.IsOpen ? "Close assistant" : setUp ? "Open assistant" : "Set up the assistant…" };
                open.Click += (s, e) => AssistantPanel.Toggle();
                menu.Items.Add(open);
                menu.Items.Add(new SeparatorMenuItem());
            }
            var link = CurrentLink();
            string status = link == Link.Down ? "● Not connected: server down"
                : link == Link.Connected ? $"● Connected (127.0.0.1:{Server.Port})"
                : $"● Ready on 127.0.0.1:{Server.Port}, no AI client yet";
            menu.Items.Add(new ButtonMenuItem { Text = status, Enabled = false });
            var busy = Dispatcher.BusyText();
            if (busy != null) menu.Items.Add(new ButtonMenuItem { Text = "   busy: " + busy, Enabled = false });
            if (link == Link.Down)
            {
                var restart = new ButtonMenuItem { Text = "Restart server" };
                restart.Click += (s, e) => { ClickAction(); Tick(); };
                menu.Items.Add(restart);
            }
            menu.Items.Add(new SeparatorMenuItem());
            var size = new ButtonMenuItem { Text = "Size" };
            for (int i = 0; i < Sizes.Length; i++)
            {
                int px = Sizes[i];
                var item = new CheckMenuItem { Text = $"{SizeNames[i]} ({px} px)", Checked = px == _size };
                item.Click += (s, e) => SetSize(px);
                size.Items.Add(item);
            }
            menu.Items.Add(size);
            var lively = new ButtonMenuItem { Text = "Liveliness" };
            string[] hints = { "never moves", "little acts in place", "dances while working", "runs all over Rhino and Grasshopper" };
            for (int i = 0; i < LevelNames.Length; i++)
            {
                int lv = i;
                var item = new CheckMenuItem { Text = $"{LevelNames[i]}: {hints[i]}", Checked = lv == _level };
                item.Click += (s, e) => SetLevel(lv);
                lively.Items.Add(item);
            }
            menu.Items.Add(lively);
            var speed = new ButtonMenuItem { Text = "Speed" };
            for (int i = 0; i < SpeedNames.Length; i++)
            {
                int si = i;
                var item = new CheckMenuItem { Text = SpeedNames[i], Checked = si == SpeedIndex };
                item.Click += (s, e) => SetSpeed(si);
                speed.Items.Add(item);
            }
            menu.Items.Add(speed);
            var hide = new ButtonMenuItem { Text = "Hide Nexiy" };
            hide.Click += (s, e) =>
            {
                SetHidden(true);
                RhinoApp.WriteLine("NextraRhinoGh: Nexiy hidden. NextraRhinoGh > Nexiy > Show brings it back.");
            };
            menu.Items.Add(hide);
            menu.Show(_canvas);
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

        // "eto,x,y" in logical pixels = the window's top-left from Rhino's bottom-right corner (same file as the old dot).
        // Older files ("off,x,y", physical pixels of the WinForms dot) fall back to the default.
        static Point LoadOffset(int size)
        {
            try { var t = File.ReadAllText(PosFile).Split(','); if (t.Length == 3 && t[0] == "eto") return new Point(int.Parse(t[1]), int.Parse(t[2])); } catch { }
            var w = NexiyArt.WindowSize(size);
            return new Point(-24 - w.Width, -14 - w.Height);
        }

        static void SaveOffset(Point p) { try { File.WriteAllText(PosFile, $"eto,{p.X},{p.Y}"); } catch { } }
    }
}
