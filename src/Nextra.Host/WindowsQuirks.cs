using System;
using System.Runtime.InteropServices;

namespace Nextra.Host
{
    /// Windows-only workaround for an Eto.Wpf behaviour, called from the shared StatusDot code (Mac: no-op stand-in in
    /// mac/Nextra.Host.Mac/MacPlatform.cs; this file is not compiled on Mac).
    /// Eto.Wpf switches on DWM blur-behind once for every window whose background or opacity is below 1
    /// (WpfWindow.SetBlurBehind → GlassHelper.BlurBehindWindow), and offers no setting to prevent it. On the transparent
    /// status dot that blur shows as a grey translucent square around the dot. Switching it off again after the window
    /// is shown leaves a clean, fully transparent window.
    static class PlatformQuirks
    {
        [StructLayout(LayoutKind.Sequential)]
        struct DWM_BLURBEHIND { public int dwFlags; public int fEnable; public IntPtr hRgnBlur; public int fTransitionOnMaximized; }
        const int DWM_BB_ENABLE = 1;
        [DllImport("dwmapi.dll")] static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DWM_BLURBEHIND blur);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;

        /// Windows 11: rounded corners (and the system's soft shadow) for a borderless window. Older Windows ignores it.
        public static void RoundCorners(Eto.Forms.Window w)
        {
            try
            {
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return;
                int v = DWMWCP_ROUND;
                DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref v, sizeof(int));
            }
            catch { }
        }

        [StructLayout(LayoutKind.Sequential)] struct MARGINS { public int L, R, T, B; }
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS m);
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_TRANSIENTWINDOW = 3;

        /// Windows 11 (22H2+): the system's acrylic blur behind the assistant window. The window's WPF background, its
        /// composition target and the WebView2 default background become transparent so the blur shows through the page
        /// (no AllowsTransparency: that would stop WebView2 from drawing). Returns "acrylic", or "none" where Windows
        /// does not support it (the page then stays solid).
        public static string Backdrop(Eto.Forms.Window w, Eto.Forms.WebView web, bool dark)
        {
            try
            {
                if (Environment.OSVersion.Version.Build < 22621) return "none";
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return "none";
                int mode = dark ? 1 : 0;
                DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref mode, sizeof(int));
                var m = new MARGINS { L = -1, R = -1, T = -1, B = -1 };
                if (DwmExtendFrameIntoClientArea(h, ref m) != 0) return "none";
                int type = DWMSBT_TRANSIENTWINDOW;
                if (DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)) != 0) return "none";
                if (w.ControlObject is System.Windows.Window ww)
                {
                    ww.Background = System.Windows.Media.Brushes.Transparent;
                    var src = System.Windows.Interop.HwndSource.FromHwnd(h);
                    if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = System.Windows.Media.Colors.Transparent;
                }
                var prop = web.ControlObject?.GetType().GetProperty("DefaultBackgroundColor");
                prop?.SetValue(web.ControlObject, System.Drawing.Color.Transparent);
                return "acrylic";
            }
            catch { return "none"; }
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int WM_NCLBUTTONDOWN = 0xA1, HTCAPTION = 2, HTBOTTOMRIGHT = 17;

        /// A borderless window moved or resized by the system's own loop, started while the mouse button is held on the page's
        /// header or grip (as if the title bar / frame corner were pressed). Mac: false (the page moves the window by deltas).
        public static bool SystemDrag(Eto.Forms.Window w, bool resize)
        {
            try
            {
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return false;
                ReleaseCapture();
                SendMessage(h, WM_NCLBUTTONDOWN, (IntPtr)(resize ? HTBOTTOMRIGHT : HTCAPTION), IntPtr.Zero);
                return true;
            }
            catch { return false; }
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000;

        /// Clicks pass through the window (Nexiy while it roams over Rhino). Only on a layered window (the transparent Eto
        /// window is one): adding the layered style here would make a window invisible.
        public static void ClickThrough(Eto.Forms.Window w, bool on)
        {
            try
            {
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return;
                long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
                if ((ex & WS_EX_LAYERED) == 0) return;
                long want = on ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
                if (want != ex) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(want));
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        static bool CtrlDown => (GetAsyncKeyState(0x11) & 0x8000) != 0;
        static readonly uint[] EditKeys = { 0x43, 0x56, 0x58, 0x41, 0x5A, 0x59 };   // C V X A Z Y

        /// Ctrl+C / V / X / A / Z / Y inside the assistant page are the page's own (copy, paste, cut, select all, undo, redo).
        /// WebView2 hands Ctrl shortcuts to its host first, and Rhino takes them as its own Copy / Paste, so text never got
        /// pasted. The page keeps these keys: WPF does not pass them on (PreviewKeyDown handled) and the browser runs them
        /// (AcceleratorKeyPressed: Handled = false). Reflection, so the plug-in needs no WebView2 reference.
        public static void KeepEditKeys(Eto.Forms.WebView web)
        {
            try
            {
                if (!(web.ControlObject is System.Windows.UIElement el)) return;
                bool IsEdit(uint vk) => CtrlDown && Array.IndexOf(EditKeys, vk) >= 0;
                el.PreviewKeyDown += (s, e) =>
                {
                    var vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key);
                    if (IsEdit(vk)) e.Handled = true;
                };
                void Hook()
                {
                    var ctl = el.GetType().GetProperty("CoreWebView2Controller", Any)?.GetValue(el);
                    var ev = ctl?.GetType().GetEvent("AcceleratorKeyPressed");
                    if (ev == null) return;
                    var mi = typeof(PlatformQuirks).GetMethod(nameof(OnAccelerator), System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                    ev.AddEventHandler(ctl, Delegate.CreateDelegate(ev.EventHandlerType, mi));
                }
                // the controller (a non-public property of the WPF WebView2) exists once WebView2 has started
                if (el.GetType().GetProperty("CoreWebView2Controller", Any)?.GetValue(el) != null) Hook();
                else
                {
                    var init = el.GetType().GetEvent("CoreWebView2InitializationCompleted");
                    EventHandler done = null;
                    done = (s, e) => { try { Hook(); } catch { } };
                    if (init != null) init.AddEventHandler(el, Delegate.CreateDelegate(init.EventHandlerType, done.Target, done.Method));
                }
            }
            catch { }
        }

        static void OnAccelerator(object sender, EventArgs e)
        {
            try
            {
                var t = e.GetType();
                var vk = Convert.ToUInt32(t.GetProperty("VirtualKey")?.GetValue(e) ?? 0u);
                if (CtrlDown && Array.IndexOf(EditKeys, vk) >= 0) t.GetProperty("Handled")?.SetValue(e, false);
            }
            catch { }
        }

        // ---- snips for the assistant's chat (Snip.cs) ----
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);

        /// the window in front with the keyboard (the snip overlay: Esc must reach it without a click first)
        public static void Foreground(Eto.Forms.Window w)
        {
            try
            {
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return;
                var fg = GetForegroundWindow();
                uint other = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero), me = GetCurrentThreadId();
                bool attached = other != 0 && other != me && AttachThreadInput(me, other, true);
                SetForegroundWindow(h);
                if (attached) AttachThreadInput(me, other, false);
            }
            catch { }
        }

        [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// the mouse on the screen, in the same pixels as GrabScreen (the snip's corners: Eto's window coordinates do not line
        /// up with them on monitors with different scaling)
        public static System.Drawing.Point? CursorScreen() => Physical(() => GetCursorPos(out var p) ? new System.Drawing.Point(p.X, p.Y) : (System.Drawing.Point?)null);

        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
        /// Rhino runs "system DPI aware": on a monitor with another scale than the main one Windows stretches its coordinates,
        /// so a screen grab there lands in the wrong place. The snip works in real pixels (per-monitor aware) instead.
        static T Physical<T>(Func<T> f)
        {
            IntPtr old = IntPtr.Zero;
            try { old = SetThreadDpiAwarenessContext(new IntPtr(-4) /* per monitor v2 */); } catch { }
            try { return f(); }
            finally { if (old != IntPtr.Zero) try { SetThreadDpiAwarenessContext(old); } catch { } }
        }

        /// the window exactly over a rectangle of the screen, in GrabScreen's pixels (the snip overlay over its monitor)
        public static void PlaceOn(Eto.Forms.Window w, System.Drawing.Rectangle r)
        {
            try { var h = w.NativeHandle; if (h != IntPtr.Zero) Physical(() => SetWindowPos(h, new IntPtr(-1) /* topmost */, r.X, r.Y, r.Width, r.Height, 0x0040 /* show */)); } catch { }
        }

        /// Esc held down, wherever the keyboard focus is (the snip overlay may not get it when another program had the front)
        public static bool EscDown() => (GetAsyncKeyState(0x1B) & 0x8000) != 0;

        /// Windows draws the snip overlay itself (Snip.cs): no system snip
        public static byte[] NativeSnip(out bool handled) { handled = false; return null; }

        delegate bool MonitorEnum(IntPtr mon, IntPtr dc, ref RECT r, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum f, IntPtr data);

        /// a picture of every monitor and where each one is (the snip covers all screens)
        static System.Collections.Generic.List<System.Drawing.Rectangle> Monitors()
        {
            var rects = new System.Collections.Generic.List<System.Drawing.Rectangle>();
            try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr d) => { rects.Add(new System.Drawing.Rectangle(r.L, r.T, r.R - r.L, r.B - r.T)); return true; }, IntPtr.Zero); } catch { }
            return rects;
        }

        /// a picture of every monitor (real pixels), where it is in real pixels, and where Rhino thinks it is (Rhino's own,
        /// possibly stretched, coordinates: the engine places Rhino's views in those)
        public static System.Collections.Generic.List<(byte[] png, System.Drawing.Rectangle phys, System.Drawing.Rectangle rhino)> GrabScreens()
        {
            var list = new System.Collections.Generic.List<(byte[], System.Drawing.Rectangle, System.Drawing.Rectangle)>();
            var mine = Monitors();                 // Rhino's coordinates (same order as below)
            return Physical(() =>
            {
                var real = Monitors();
                for (int i = 0; i < real.Count; i++)
                {
                    var r = real[i];
                    try
                    {
                        using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
                        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                        using var ms = new System.IO.MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        list.Add((ms.ToArray(), r, i < mine.Count ? mine[i] : r));
                    }
                    catch { }
                }
                return list;
            });
        }

        /// a picture of the whole monitor Rhino is on (physical pixels) and where that monitor is
        public static (byte[] png, System.Drawing.Rectangle phys)? GrabScreen(IntPtr rhinoWindow)
        {
            try
            {
                var mon = MonitorFromWindow(rhinoWindow, 2 /* nearest */);
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (!GetMonitorInfo(mon, ref mi)) return null;
                var r = new System.Drawing.Rectangle(mi.rcMonitor.L, mi.rcMonitor.T, mi.rcMonitor.R - mi.rcMonitor.L, mi.rcMonitor.B - mi.rcMonitor.T);
                using var bmp = new System.Drawing.Bitmap(r.Width, r.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(r.X, r.Y, 0, 0, r.Size);
                using var ms = new System.IO.MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return (ms.ToArray(), r);
            }
            catch { return null; }
        }

        /// a part of a PNG, as PNG
        public static byte[] CropPng(byte[] png, int x, int y, int w, int h)
        {
            try
            {
                using var src = new System.Drawing.Bitmap(new System.IO.MemoryStream(png));
                var r = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(x, y, w, h), new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
                if (r.Width < 2 || r.Height < 2) return null;
                using var part = src.Clone(r, src.PixelFormat);
                using var ms = new System.IO.MemoryStream();
                part.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
            catch { return null; }
        }

        public static void AfterShow(Eto.Forms.Window w)
        {
            try
            {
                var h = w.NativeHandle;
                if (h == IntPtr.Zero) return;
                var b = new DWM_BLURBEHIND { dwFlags = DWM_BB_ENABLE, fEnable = 0 };
                DwmEnableBlurBehindWindow(h, ref b);
            }
            catch { }   // cosmetic only: a grey square is better than no dot
        }
    }
}
