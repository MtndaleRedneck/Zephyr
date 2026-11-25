using System;
using System.Globalization;
using System.Linq;
using System.Windows;

namespace ZephyrPlayer
{
    /// <summary>Remembered window position and size (2.0.2 behavior): one geometry shared by Home and the
    /// player window, saved whenever either hides or closes, applied whenever either shows.</summary>
    internal static class WindowGeometry
    {
        internal static string FormatBounds(Rect r, bool maximized) => string.Join(",",
            new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))) + (maximized ? ",1" : ",0");

        internal static (Rect Bounds, bool Maximized)? ParseBounds(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',');
            if (parts.Length != 5) return null;
            var n = new double[4];
            for (var i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || double.IsNaN(n[i]) || double.IsInfinity(n[i])) return null;
            if (n[2] < 200 || n[3] < 150 || n[2] > 20000 || n[3] > 20000) return null;
            return (new Rect(n[0], n[1], n[2], n[3]), parts[4] == "1");
        }

        /// <summary>A saved rect is used only when enough of it lands on today's desktop; otherwise null
        /// (the window keeps its own default size and position). Pure, for tests.</summary>
        internal static Rect? ChooseBounds(Rect? saved, Rect virtualScreen)
        {
            if (saved is not Rect s) return null;
            var visible = Rect.Intersect(s, virtualScreen);
            if (visible.IsEmpty || visible.Width < 160 || visible.Height < 120) return null;
            var w = Math.Min(s.Width, virtualScreen.Width);
            var h = Math.Min(s.Height, virtualScreen.Height);
            // Clamp the origin AFTER the size clamp so the result always lies inside today's desktop
            // (a rect saved on a larger or since-removed monitor can start left of or above the current origin).
            var left = Math.Max(virtualScreen.Left, Math.Min(s.Left, virtualScreen.Right - w));
            var top = Math.Max(virtualScreen.Top, Math.Min(s.Top, virtualScreen.Bottom - h));
            return new Rect(left, top, w, h);
        }

        internal static void Attach(Window w, PlayerSettings settings)
        {
            Apply(w, settings);
            w.IsVisibleChanged += (_, e) =>
            {
                if (e.NewValue is true) Apply(w, settings); else Save(w, settings);
            };
            w.Closing += (_, __) => Save(w, settings);
        }

        private static bool Fullscreen(Window w) => w is MainWindow m && m.IsFullscreen;

        internal static void Apply(Window w, PlayerSettings settings)
        {
            try
            {
                if (Fullscreen(w)) return;
                var saved = ParseBounds(settings.ShellBounds);
                var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                var r = ChooseBounds(saved?.Bounds, virt);
                if (r is not Rect rect) return;
                w.WindowStartupLocation = WindowStartupLocation.Manual;
                if (w.WindowState != WindowState.Normal) w.WindowState = WindowState.Normal;
                w.Left = rect.Left; w.Top = rect.Top; w.Width = rect.Width; w.Height = rect.Height;
                if (saved?.Maximized == true) w.WindowState = WindowState.Maximized;
            }
            catch { }
        }

        internal static void Save(Window w, PlayerSettings settings)
        {
            try
            {
                if (!w.IsVisible && !w.IsLoaded) return;
                var fs = Fullscreen(w);
                var restore = w.WindowState == WindowState.Normal && !fs ? new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight) : w.RestoreBounds;
                if (restore.IsEmpty || restore.Width < 200 || restore.Height < 150) return;
                var text = FormatBounds(restore, w.WindowState == WindowState.Maximized && !fs);
                if (text != settings.ShellBounds) settings.ShellBounds = text;
            }
            catch { }
        }
    }
}
