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
        public FrameworkElement? CurrentView => _current;
        private FrameworkElement? _current;
        /// <summary>The library kept drawn under an incoming player until its start has
        /// something on screen (its own Playing, or the error panel).</summary>
        private FrameworkElement? _heldUnder;
        internal static readonly SolidColorBrush PlayerCaption = Freeze(new SolidColorBrush(Colors.Black));
        internal static readonly SolidColorBrush LibraryCaption = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0C)));
        private static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }
        internal FrameworkElement? HeldUnderView => _heldUnder;
        internal int ViewHostChildCount => ViewHost.Children.Count;

        /// <summary>The native handle (stable for the window's life; CI asserts it).</summary>
        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        /// <summary>Swap the view in place; the window itself never hides, moves or
        /// resizes. CP1 repair 3 (the view-transition blink: two all-black content
        /// frames, library->player ~688 ms and Back ~406 ms, run 41026623817): the
        /// outgoing view is never taken off screen before the incoming one has
        /// something to show.
        /// - library -> player: the player is added OVER the library with a
        ///   transparent background, and the library stays drawn under it until the
        ///   player's start settles (its own Playing, or the error panel). The video
        ///   host is a native child window that WPF cannot draw over or under, so it
        ///   paints a picture of the library (not black) for the same span.
        /// - player -> library (Back): the library is laid out UNDER the player first
        ///   and the video host is repainted with its picture; only then does
        ///   [beforeOldLeaves] stop the movie and hide the overlay, and the player
        ///   leaves. Whatever is uncovered on the way is the library.
        /// The old view leaves the visual tree (its Unloaded runs) as before.</summary>
        public void ShowView(FrameworkElement view, Action? beforeOldLeaves = null)
        {
            var old = _current;
            if (ReferenceEquals(old, view)) { beforeOldLeaves?.Invoke(); return; }
            // Item 44 (render 02): the caption is black over the player; the library keeps
            // render 01's #0A0A0C.
            CaptionBar.Background = view is PlayerView ? PlayerCaption : LibraryCaption;
            if (old is PlayerView && view is not PlayerView)
            {
                // Back. The library may still be held under the player (Back pressed
                // while the start was loading): it is already in the tree.
                if (!ViewHost.Children.Contains(view)) ViewHost.Children.Insert(0, view);
                view.IsHitTestVisible = true;
                _heldUnder = null;
                view.UpdateLayout();
                HoldVideoHost(view);
                beforeOldLeaves?.Invoke();
                // The movie's own video window is gone now: the host's whole client
                // paints the library picture (it clips its children, so the paint
                // above could not reach under the video window).
                RepaintVideoHost();
                if (old is PlayerView p) p.SetSwitchTransparent(false);
                ViewHost.Children.Remove(old);
                _current = view;
                ReleaseVideoHost();
                return;
            }
            if (old != null && view is PlayerView player && old is not PlayerView)
            {
                // library -> player: the library stays drawn under the player.
                old.IsHitTestVisible = false;
                HoldVideoHost(old);
                player.SetSwitchTransparent(true);
                if (!ViewHost.Children.Contains(player)) ViewHost.Children.Add(player);
                _heldUnder = old;
                _current = player;
                beforeOldLeaves?.Invoke();
                player.StartSettled -= OnPlayerStartSettled;
                player.StartSettled += OnPlayerStartSettled;
                return;
            }
            beforeOldLeaves?.Invoke();
            ViewHost.Children.Clear();
            _heldUnder = null;
            ViewHost.Children.Add(view);
            _current = view;
        }

        /// <summary>The incoming player now shows its own content (first frame or the
        /// error panel): the held library leaves, the video host goes back to black.</summary>
        private void OnPlayerStartSettled()
        {
            if (_current is not PlayerView player) return;
            player.StartSettled -= OnPlayerStartSettled;
            var held = _heldUnder; _heldUnder = null;
            player.SetSwitchTransparent(false);
            if (held != null && !ReferenceEquals(held, _current))
            {
                held.IsHitTestVisible = true;
                ViewHost.Children.Remove(held);
            }
            ReleaseVideoHost();
        }

        // ---------- the native video host during a switch ----------

        private IntPtr _holdBrush, _holdBitmap;
        internal bool VideoHostHeld => _holdBrush != IntPtr.Zero;

        /// <summary>The video host (LibVLCSharp's native "static" child) paints [view]'s
        /// picture instead of black until ReleaseVideoHost.</summary>
        private void HoldVideoHost(FrameworkElement view)
        {
            try
            {
                var w = view.ActualWidth; var h = view.ActualHeight;
                if (w < 1 || h < 1) return;
                var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int pw = (int)Math.Ceiling(w * m.M11), ph = (int)Math.Ceiling(h * m.M22);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) }, null, new Rect(0, 0, w, h));
                var rtb = new RenderTargetBitmap(pw, ph, 96 * m.M11, 96 * m.M22, PixelFormats.Pbgra32);
                rtb.Render(dv);
                var pixels = new byte[pw * ph * 4];
                rtb.CopyPixels(pixels, pw * 4, 0);
                var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = pw, biHeight = -ph, biPlanes = 1, biBitCount = 32 };
                var bmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) return;
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var brush = CreatePatternBrush(bmp);
                if (brush == IntPtr.Zero) { DeleteObject(bmp); return; }
                FreeHold();
                _holdBitmap = bmp; _holdBrush = brush;
                RepaintVideoHost();
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("video host hold failed: " + ex.GetType().Name); }
        }

        private void ReleaseVideoHost()
        {
            if (_holdBrush == IntPtr.Zero) return;
            FreeHold();
            RepaintVideoHost();
        }

        private void FreeHold()
        {
            var brush = _holdBrush; var bmp = _holdBitmap;
            _holdBrush = _holdBitmap = IntPtr.Zero;
            if (brush != IntPtr.Zero) DeleteObject(brush);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
        }

        /// <summary>The native video host child, if the player has created one.</summary>
        internal IntPtr VideoHostHandle()
        {
            var host = IntPtr.Zero;
            var me = Handle;
            if (me == IntPtr.Zero) return host;
            EnumChildWindows(me, (h, _) =>
            {
                var sb = new System.Text.StringBuilder(32); GetClassNameW(h, sb, 32);
                if (sb.ToString().Equals("Static", StringComparison.OrdinalIgnoreCase) && GetParent(h) == me) { host = h; return false; }
                return true;
            }, IntPtr.Zero);
            return host;
        }

        private void RepaintVideoHost()
        {
            var host = VideoHostHandle();
            // Its own client only (no RDW_ALLCHILDREN: libVLC's video window inside it
            // keeps its picture), painted now rather than at the next message pump.
            if (host != IntPtr.Zero) RedrawWindow(host, IntPtr.Zero, IntPtr.Zero, 0x0001 /*INVALIDATE*/ | 0x0004 /*ERASE*/ | 0x0100 /*UPDATENOW*/);
        }

        [StructLayout(LayoutKind.Sequential)] private struct BITMAPINFOHEADER { public uint biSize; public int biWidth, biHeight; public ushort biPlanes, biBitCount; public uint biCompression, biSizeImage; public int biXPelsPerMeter, biYPelsPerMeter; public uint biClrUsed, biClrImportant; }
        [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreatePatternBrush(IntPtr bitmap);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        private delegate bool EnumChildProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, System.Text.StringBuilder sb, int max);
        [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr region, uint flags);

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

        // The player's video host is a native "static" child of this window (LibVLCSharp
        // VideoView). Until libVLC's own video window paints into it, Windows paints it
        // in the light system button-face grey: CI flash sampling (run 41019817391) saw
        // one light frame in the player switch. Black is the only right colour behind
        // a movie, so this window answers the child's colour query with black.
        private const int WM_CTLCOLORSTATIC = 0x0138;
        [DllImport("gdi32.dll")] private static extern IntPtr GetStockObject(int index);
        [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr hdc, uint color);
        internal static readonly IntPtr BlackBrush = GetStockObject(4 /* BLACK_BRUSH */);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CTLCOLORSTATIC)
            {
                SetBkColor(wParam, 0x000000);
                handled = true;
                // During a view switch: the outgoing library's picture (ShowView).
                return _holdBrush != IntPtr.Zero ? _holdBrush : BlackBrush;
            }
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
