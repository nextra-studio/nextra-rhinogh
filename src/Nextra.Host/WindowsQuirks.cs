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
