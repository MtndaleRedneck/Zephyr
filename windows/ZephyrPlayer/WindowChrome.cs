using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZephyrPlayer
{
    /// <summary>
    /// Dark window chrome: asks DWM for the immersive dark title bar so the
    /// native frame matches the app's dark surfaces. No-op where unsupported.
    /// </summary>
    public static class WindowChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void ApplyDarkTitleBar(Window window)
        {
            window.SourceInitialized += (_, __) =>
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                int on = 1;
                // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (1903+); 19 = the older id.
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
                int backdrop = 2; // DWMSBT_MAINWINDOW, unsupported OS ignores it.
                DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            };
        }
    }
}
