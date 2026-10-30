using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZephyrPlayer
{
    /// <summary>
    /// The one top-level window for the app's life (2.0, P0-1 and P0-2). The library
    /// and the player are views swapped in place inside it: no Home.Hide/Player.Show
    /// pair, one handle, one size and position shared by every view. Close goes to
    /// the tray (the App decides; ShutdownMode stays OnExplicitShutdown).
    /// </summary>
    public partial class ShellWindow : Window
    {
        private readonly PlayerSettings _settings;
        private bool _fullscreen;
        private WindowState _stateBeforeFullscreen = WindowState.Normal;

        /// <summary>Keys arriving at the shell, routed by the App to the current view.</summary>
        public event KeyEventHandler? ViewKeyDown;

        public ShellWindow(PlayerSettings settings)
        {
            InitializeComponent();
            _settings = settings;
            ZephyrPlayer.WindowChrome.ApplyDarkTitleBar(this);
            CaptionIcon.Source = IconFrame(36);
            SourceInitialized += (_, __) =>
            {
                var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                source?.AddHook(WndProc);
            };
            StateChanged += (_, __) => UpdateChromeForState();
            PreviewKeyDown += (_, e) => ViewKeyDown?.Invoke(this, e);
            RestoreBoundsFromSettings();
        }

        /// <summary>The view currently on screen (library or player).</summary>
        public FrameworkElement? CurrentView => ViewHost.Content as FrameworkElement;

        /// <summary>The native handle (stable for the window's life; CI asserts it).</summary>
        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        /// <summary>Swap the view in place. The old view leaves the visual tree (its
        /// Unloaded runs), the window itself never hides, moves or resizes.</summary>
        public void ShowView(FrameworkElement view)
        {
            if (ReferenceEquals(ViewHost.Content, view)) return;
            ViewHost.Content = view;
        }

        public bool IsFullscreen => _fullscreen;

        /// <summary>Full screen is window chrome, so the shell owns it: no caption bar,
        /// no resize border, the whole monitor (see WM_GETMINMAXINFO below).</summary>
        private ResizeMode _resizeBeforeFullscreen = ResizeMode.CanResize;
        public void ApplyFullscreen(bool on)
        {
            if (on == _fullscreen) return;
            var chrome = System.Windows.Shell.WindowChrome.GetWindowChrome(this);
            if (on)
            {
                _stateBeforeFullscreen = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
                _fullscreen = true;
                _resizeBeforeFullscreen = ResizeMode;
                ResizeMode = ResizeMode.NoResize; // no thick frame: DWM bounds = the monitor
                CaptionRow.Height = new GridLength(0);
                CaptionBar.Visibility = Visibility.Collapsed;
                if (chrome != null) { chrome.CaptionHeight = 0; chrome.ResizeBorderThickness = new Thickness(0); }
                // Re-enter Maximized so Windows asks WM_GETMINMAXINFO again (monitor rect now).
                if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                _fullscreen = false;
                ResizeMode = _resizeBeforeFullscreen;
                CaptionRow.Height = new GridLength(32);
                CaptionBar.Visibility = Visibility.Visible;
                if (chrome != null) { chrome.CaptionHeight = 32; chrome.ResizeBorderThickness = new Thickness(6); }
                WindowState = WindowState.Normal;
                if (_stateBeforeFullscreen == WindowState.Maximized) WindowState = WindowState.Maximized;
            }
            UpdateChromeForState();
        }

        private void UpdateChromeForState()
        {
            var max = WindowState == WindowState.Maximized;
            MaximizeButton.ToolTip = max ? "Restore" : "Maximize";
            MaximizeGlyph.Data = Geometry.Parse(max
                ? "M2.5,2.5 H8.5 V8.5 H2.5 Z M2.5,1.5 V0.5 H9.5 V7.5 H8.5"
                : "M2,0.5 H8 A1.5,1.5 0 0 1 9.5,2 V8 A1.5,1.5 0 0 1 8,9.5 H2 A1.5,1.5 0 0 1 0.5,8 V2 A1.5,1.5 0 0 1 2,0.5 Z");
        }

        private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void OnCloseButton(object sender, RoutedEventArgs e) => Close();

        // ---------- remembered geometry (one window, one geometry for every view) ----------

        internal static string FormatBounds(Rect r, bool maximized) => string.Join(",",
            new[] { r.Left, r.Top, r.Width, r.Height }.Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))) + (maximized ? ",1" : ",0");

        internal static (Rect Bounds, bool Maximized)? ParseBounds(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text.Split(',');
            if (parts.Length != 5) return null;
            var n = new double[4];
            for (var i = 0; i < 4; i++) if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i]) || double.IsNaN(n[i]) || double.IsInfinity(n[i])) return null;
            if (n[2] < 200 || n[3] < 150 || n[2] > 20000 || n[3] > 20000) return null;
            return (new Rect(n[0], n[1], n[2], n[3]), parts[4] == "1");
        }

        /// <summary>A saved rect is used only when enough of it lands on today's
        /// desktop; otherwise the default (1600x1000 when it fits) is centered on the
        /// work area. Pure, for tests.</summary>
        internal static Rect ChooseBounds(Rect? saved, Rect workArea, Rect virtualScreen)
        {
            if (saved is Rect s)
            {
                var visible = Rect.Intersect(s, virtualScreen);
                if (!visible.IsEmpty && visible.Width >= 160 && visible.Height >= 120)
                {
                    var w = Math.Min(s.Width, virtualScreen.Width);
                    var h = Math.Min(s.Height, virtualScreen.Height);
                    return new Rect(s.Left, s.Top, w, h);
                }
            }
            // Render 01's 1600x1000 whenever it fits with a small margin (a 1080p
            // monitor's 1040 px work area takes the full 1000); smaller screens get 94%.
            var dw = workArea.Width >= 1600 + 48 ? 1600 : Math.Floor(workArea.Width * 0.94);
            var dh = workArea.Height >= 1000 + 24 ? 1000 : Math.Floor(workArea.Height * 0.94);
            return new Rect(workArea.Left + Math.Floor((workArea.Width - dw) / 2), workArea.Top + Math.Floor((workArea.Height - dh) / 2), dw, dh);
        }

        private void RestoreBoundsFromSettings()
        {
            var saved = ParseBounds(_settings.ShellBounds);
            var work = SystemParameters.WorkArea;
            var virt = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var r = ChooseBounds(saved?.Bounds, work, virt);
            Left = r.Left; Top = r.Top; Width = r.Width; Height = r.Height;
            if (saved?.Maximized == true) WindowState = WindowState.Maximized;
        }

        /// <summary>Saved on every hide/close path (tray close, quit).</summary>
        public void SaveBounds()
        {
            try
            {
                var restore = WindowState == WindowState.Normal && !_fullscreen ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
                if (restore.IsEmpty || restore.Width < 200 || restore.Height < 150) return;
                var text = FormatBounds(restore, WindowState == WindowState.Maximized && !_fullscreen);
                if (text != _settings.ShellBounds) _settings.ShellBounds = text;
            }
            catch { }
        }

        // ---------- icon frame (the approved .ico, picked at the size it is drawn) ----------

        internal static ImageSource? IconFrame(int pixels)
        {
            try
            {
                var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/ZephyrPlayer;component/assets/icon.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.Where(f => f.PixelWidth >= pixels).OrderBy(f => f.PixelWidth).FirstOrDefault()
                    ?? decoder.Frames.OrderByDescending(f => f.PixelWidth).First();
                frame.Freeze();
                return frame;
            }
            catch { return null; }
        }

        // ---------- maximize / full screen: work area vs whole monitor ----------

        private const int WM_GETMINMAXINFO = 0x0024;
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_GETMINMAXINFO) return IntPtr.Zero;
            var monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor == IntPtr.Zero) return IntPtr.Zero;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return IntPtr.Zero;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            var area = _fullscreen ? info.rcMonitor : info.rcWork;
            mmi.ptMaxPosition.X = area.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = area.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = area.Right - area.Left;
            mmi.ptMaxSize.Y = area.Bottom - area.Top;
            if (_fullscreen)
            {
                mmi.ptMaxTrackSize.X = Math.Max(mmi.ptMaxTrackSize.X, mmi.ptMaxSize.X);
                mmi.ptMaxTrackSize.Y = Math.Max(mmi.ptMaxTrackSize.Y, mmi.ptMaxSize.Y);
            }
            Marshal.StructureToPtr(mmi, lParam, true);
            return IntPtr.Zero;
        }
    }
}
