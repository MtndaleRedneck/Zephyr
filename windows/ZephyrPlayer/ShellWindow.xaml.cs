using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
                SwitchTrace.WatchShell(new WindowInteropHelper(this).Handle);
            };
            StateChanged += (_, __) => UpdateChromeForState();
            PreviewKeyDown += (_, e) => ViewKeyDown?.Invoke(this, e);
            RestoreBoundsFromSettings();
        }

        /// <summary>The view currently on screen (library or player).</summary>
        public FrameworkElement? CurrentView => _current;
        private FrameworkElement? _current;
        /// <summary>The library kept drawn under an incoming player until its start has
        /// settled (see OnPlayerStartSettled for the exact signals), or the error panel shows.</summary>
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
        ///   player's start settles (exact signals: OnPlayerStartSettled). The video
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
                SwitchTrace.Mark("back.begin");
                if (!ViewHost.Children.Contains(view)) ViewHost.Children.Insert(0, view);
                view.IsHitTestVisible = true;
                _heldUnder = null;
                view.UpdateLayout();
                SwitchTrace.Mark("library.laid-out", $"{view.ActualWidth:0}x{view.ActualHeight:0}");
                HoldVideoHost(view);
                var leaving = (PlayerView)old;
                // The WPF surface under the native host must show the library too: the
                // player turns see-through over it and stops taking clicks.
                leaving.SetSwitchTransparent(true);
                leaving.IsHitTestVisible = false;
                SwitchTrace.Mark("player.see-through");
                beforeOldLeaves?.Invoke();
                // After the stop (and the overlay's hide) libVLC's video window(s) are
                // gone, checked rather than assumed, and the host paints the library
                // picture over its whole client (it clips its children, so the paint
                // above could not reach under the video window).
                var host = VideoHostHandle();
                WaitVideoWindowsGone(host);
                RepaintVideoHost();
                GdiFlush();
                TeardownPaintedForTest?.Invoke(host);
                _current = view;
                // The player leaves, and the host is hidden and lets go of the library
                // picture, only once the screen has been read back and shows (a)
                // the library picture where the host is and (b) WPF's own switched frame
                // (the caption in the library's colour, drawn in the same frame as the
                // library under the see-through player). Rendering callbacks and timers
                // are no longer taken as presentation (run 41095411723 attempt 1: one
                // all-black client frame at 438 ms of Back).
                StartPresentedHandoff(leaving, host);
                return;
            }
            CancelPendingLeave(view as PlayerView);
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

        /// <summary>The incoming player's start has settled (PlayerView.OnNativeContent:
        /// after its own Playing, Buffering 100 % or a positive movie clock, or the 45 s
        /// quiet fallback; or the error panel). No rendered picture is observed here. The
        /// held library leaves, the video host goes back to black.</summary>
        private void OnPlayerStartSettled()
        {
            if (_current is not PlayerView player) { SwitchTrace.Mark("settled.ignored", "current is not the player"); return; }
            SwitchTrace.Mark("settled.release-hold");
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
        private byte[]? _holdPixels; private int _holdW, _holdH;
        internal bool VideoHostHeld => _holdBrush != IntPtr.Zero;

        /// <summary>The video host (LibVLCSharp's native "static" child) paints [view]'s
        /// picture instead of black until ReleaseVideoHost.</summary>
        private void HoldVideoHost(FrameworkElement view)
        {
            try
            {
                var w = view.ActualWidth; var h = view.ActualHeight;
                if (w < 1 || h < 1) { SwitchTrace.Mark("hold.skipped", $"view {w:0}x{h:0}"); return; }
                var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
                int pw = (int)Math.Ceiling(w * m.M11), ph = (int)Math.Ceiling(h * m.M22);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(view) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, w, h) }, null, new Rect(0, 0, w, h));
                var pixels = new byte[pw * ph * 4];
                double drawn = 0;
                // The picture must really be drawn (the library's own background is
                // opaque, so a drawn picture is opaque everywhere): one more render if
                // not; a picture still not drawn is kept but never counted as proof.
                for (int attempt = 0; attempt < 2 && drawn < 99; attempt++)
                {
                    if (attempt > 0) view.UpdateLayout();
                    var rtb = new RenderTargetBitmap(pw, ph, 96 * m.M11, 96 * m.M22, PixelFormats.Pbgra32);
                    rtb.Render(dv);
                    rtb.CopyPixels(pixels, pw * 4, 0);
                    drawn = OpaquePercent(pixels);
                }
                var bi = new BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = pw, biHeight = -ph, biPlanes = 1, biBitCount = 32 };
                var bmp = CreateDIBSection(IntPtr.Zero, ref bi, 0, out var bits, IntPtr.Zero, 0);
                if (bmp == IntPtr.Zero) { SwitchTrace.Mark("hold.failed", "CreateDIBSection"); return; }
                Marshal.Copy(pixels, 0, bits, pixels.Length);
                var brush = CreatePatternBrush(bmp);
                if (brush == IntPtr.Zero) { DeleteObject(bmp); SwitchTrace.Mark("hold.failed", "CreatePatternBrush"); return; }
                FreeHold();
                _holdBitmap = bmp; _holdBrush = brush;
                _holdPixels = drawn >= 99 ? pixels : null; _holdW = pw; _holdH = ph;
                if (SwitchTrace.On) SwitchTrace.Mark(drawn >= 99 ? "hold.set" : "hold.set-NOT-DRAWN", $"{pw}x{ph} opaque={drawn:0.0}% non-black={NonBlackPercent(pixels):0.0}%");
                RepaintVideoHost();
            }
            catch (Exception ex) { SwitchTrace.Mark("hold.failed", ex.GetType().Name); System.Diagnostics.Trace.TraceWarning("video host hold failed: " + ex.GetType().Name); }
        }

        /// <summary>Share of sampled pixels that are fully opaque (drawn).</summary>
        internal static double OpaquePercent(byte[] bgra)
        {
            int n = 0, op = 0;
            for (int o = 3; o < bgra.Length; o += 4 * 61) { n++; if (bgra[o] == 255) op++; }
            return n == 0 ? 0 : 100.0 * op / n;
        }

        /// <summary>Share of sampled pixels that are not black (any channel above 24).</summary>
        internal static double NonBlackPercent(byte[] bgra)
        {
            int n = 0, lit = 0;
            for (int o = 0; o + 3 < bgra.Length; o += 4 * 61) { n++; if (bgra[o] > 24 || bgra[o + 1] > 24 || bgra[o + 2] > 24) lit++; }
            return n == 0 ? 0 : 100.0 * lit / n;
        }

        // ---------- Back: the player leaves after the library is on screen ----------

        private int _leaveGen;
        private PlayerView? _leaving;
        private IntPtr _leavingHost;
        internal bool LeavePending => _leaving != null;
        /// <summary>The last Back's handoff verdict ("proven", or why not), for tests.</summary>
        internal string LastHandoff { get; private set; } = "";
        /// <summary>Upper bound on the wait for the screen proofs. A shell that is hidden,
        /// minimized or covered is not waited on at all (nothing of it is on screen);
        /// this bound only stops a visible screen that never shows the library from
        /// holding the old player forever, and the result is recorded as unproven.</summary>
        internal static int MaxPresentWaitMs = 1500;
        /// <summary>Test seam: runs right after the post-teardown repaint of the host,
        /// with the host handle (a test paints the host black here, as a stale video
        /// remnant would be).</summary>
        internal Action<IntPtr>? TeardownPaintedForTest;

        private void StartPresentedHandoff(PlayerView leaving, IntPtr host)
        {
            var gen = ++_leaveGen;
            _leaving = leaving;
            _leavingHost = host;
            var shell = Handle;
            // What the screen must show, captured here on the UI thread.
            var pic = _holdPixels; int pw = _holdW, ph = _holdH;
            int hx = 0, hy = 0, hw = 0, hh = 0;
            if (pic != null && host != IntPtr.Zero && GetWindowRect(host, out var hr))
            {
                hx = hr.Left; hy = hr.Top; hw = Math.Min(pw, hr.Right - hr.Left); hh = Math.Min(ph, hr.Bottom - hr.Top);
            }
            bool hostCheck = pic != null && hw >= 4 && hh >= 4;
            bool pictureMissing = _holdBrush == IntPtr.Zero || pic == null;
            int cx = 0, cy = 0, cw = 0, ch = 0;
            bool captionCheck = false;
            if (CaptionBar.IsVisible && CaptionBar.ActualWidth > 8 && CaptionBar.ActualHeight > 3 && PresentationSource.FromVisual(CaptionBar) != null)
            {
                var tl = CaptionBar.PointToScreen(new Point(0, 0));
                var br = CaptionBar.PointToScreen(new Point(CaptionBar.ActualWidth, CaptionBar.ActualHeight));
                cx = (int)Math.Ceiling(tl.X); cy = (int)Math.Ceiling(tl.Y); cw = (int)Math.Floor(br.X) - cx; ch = (int)Math.Floor(br.Y) - cy;
                captionCheck = cw > 8 && ch > 3;
            }
            int libColor = ToRgb(LibraryCaption.Color), playerColor = ToRgb(PlayerCaption.Color);
            SwitchTrace.Mark("handoff.begin", $"host-check={hostCheck} caption-check={captionCheck}");
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool hostOk = !hostCheck, wpfOk = !captionCheck;
                string hostWord = hostCheck ? "waiting" : (pictureMissing ? "LIBRARY PICTURE MISSING OR NOT DRAWN" : "no host on screen to check");
                if (pictureMissing && host != IntPtr.Zero) hostOk = false;
                string wpfWord = captionCheck ? "waiting" : "no caption on screen";
                int repaints = 0;
                try
                {
                    while (gen == _leaveGen && !(hostOk && wpfOk) && !(pic == null && wpfOk) && sw.ElapsedMilliseconds < MaxPresentWaitMs)
                    {
                        ScreenProof.WaitForComposedFrame();
                        if (!hostOk && pic != null)
                        {
                            // The host's place now (a window leaving full screen may still move).
                            if (GetWindowRect(host, out var nr)) { hx = nr.Left; hy = nr.Top; hw = Math.Min(pw, nr.Right - nr.Left); hh = Math.Min(ph, nr.Bottom - nr.Top); }
                            var r = ScreenProof.PictureShown(shell, hx, hy, hw, hh, pic!, pw);
                            if (r.Verdict != ScreenProof.Verdict.Mismatch) { hostOk = true; hostWord = r.ToString(); SwitchTrace.Mark("presented.host", "after-teardown: " + r); }
                            else
                            {
                                SwitchTrace.Mark("presented.host-mismatch", "after-teardown: " + r);
                                if (SwitchTrace.On) ScreenProof.DumpMismatch(hx, hy, hw, hh, pic!, pw);
                                if (repaints++ < 20) Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() => { if (gen == _leaveGen) { RepaintVideoHost(); GdiFlush(); } }));
                            }
                        }
                        if (!wpfOk)
                        {
                            var c = ScreenProof.StripShows(shell, cx, cy, cw, ch, libColor, playerColor);
                            if (c.Verdict != ScreenProof.Verdict.Mismatch) { wpfOk = true; wpfWord = c.ToString(); SwitchTrace.Mark("presented.wpf", c.ToString()); }
                        }
                    }
                }
                catch (Exception ex) { hostWord += " (" + ex.GetType().Name + ")"; }
                var verdict = hostOk && wpfOk ? "proven" : $"unproven after {sw.ElapsedMilliseconds} ms (host: {hostWord}; wpf frame: {wpfWord})";
                if (!(hostOk && wpfOk)) System.Diagnostics.Trace.TraceWarning("Back handoff " + verdict);
                SwitchTrace.Mark("handoff.verdict", verdict);
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => CompleteLeave(gen, verdict)));
            });
        }

        private static int ToRgb(Color c) => (c.R << 16) | (c.G << 8) | c.B;

        /// <summary>Order, after the screen proofs: the player leaves the tree (its
        /// transparency is reset), the host is hidden (WPF parks it on removal; if it
        /// is still visible the shell hides it itself), and only then is the library
        /// picture let go. The host is never painted black while it can be seen.</summary>
        private void CompleteLeave(int gen, string verdict)
        {
            if (gen != _leaveGen) return;
            var leaving = _leaving; _leaving = null;
            var host = _leavingHost; _leavingHost = IntPtr.Zero;
            if (leaving == null || ReferenceEquals(_current, leaving)) return;
            LastHandoff = verdict;
            ViewHost.Children.Remove(leaving);
            SwitchTrace.Mark("player.removed");
            leaving.SetSwitchTransparent(false);
            leaving.IsHitTestVisible = true;
            if (host != IntPtr.Zero && IsWindow(host) && IsWindowVisible(host))
            {
                SetWindowPos(host, IntPtr.Zero, 0, 0, 0, 0, 0x0080 /*HIDEWINDOW*/ | 0x0001 /*NOSIZE*/ | 0x0002 /*NOMOVE*/ | 0x0004 /*NOZORDER*/ | 0x0010 /*NOACTIVATE*/);
                SwitchTrace.Mark("host.hidden-by-shell");
            }
            bool hidden = host == IntPtr.Zero || !IsWindow(host) || !IsWindowVisible(host);
            SwitchTrace.Mark("hold.released", hidden ? "host-hidden" : "HOST-STILL-VISIBLE");
            FreeHold();
        }

        /// <summary>libVLC's video window(s) inside the host: gone after its stop. Waited
        /// on (at most 100 ms) rather than assumed, so the host's paint covers its
        /// whole client.</summary>
        private static void WaitVideoWindowsGone(IntPtr host)
        {
            if (host == IntPtr.Zero) return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int n;
            while ((n = VisibleChildren(host)) > 0 && sw.ElapsedMilliseconds < 100) System.Threading.Thread.Sleep(1);
            SwitchTrace.Mark("video-windows.after-stop", $"{n} visible after {sw.ElapsedMilliseconds} ms");
        }

        private static int VisibleChildren(IntPtr host)
        {
            int n = 0;
            EnumChildWindows(host, (h, _) => { if (IsWindowVisible(h)) n++; return true; }, IntPtr.Zero);
            return n;
        }

        /// <summary>Any other switch overrides a pending Back. A player coming straight
        /// back is the same view still in the tree: it is restored, not removed.</summary>
        private void CancelPendingLeave(PlayerView? incoming)
        {
            ++_leaveGen;
            var leaving = _leaving; _leaving = null;
            _leavingHost = IntPtr.Zero;
            if (leaving == null) return;
            leaving.IsHitTestVisible = true;
            if (!ReferenceEquals(leaving, incoming))
            {
                ViewHost.Children.Remove(leaving);
                leaving.SetSwitchTransparent(false);
            }
        }

        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("gdi32.dll")] private static extern bool GdiFlush();

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
            _holdPixels = null;
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
            if (SwitchTrace.On) SwitchTrace.Mark("host.repaint", $"host={(host == IntPtr.Zero ? "none" : "found")} visible-video-windows={SwitchTrace.VisibleVideoChildren(host)} hold={_holdBrush != IntPtr.Zero}");
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
            MarkFullscreenForTaskbar(on);
        }

        /// <summary>Render 03 is edge to edge: the taskbar must not sit over the bottom of
        /// a fullscreen movie. Windows lowers the taskbar for a window it judges
        /// fullscreen; ITaskbarList2.MarkFullscreenWindow says so explicitly, for the
        /// shell and for the windows it owns (LibVLCSharp's overlay with the bar, which
        /// is the active window after a click on the bar). Best effort: a refusal leaves
        /// Windows' own judgement.</summary>
        private void MarkFullscreenForTaskbar(bool on)
        {
            try
            {
                var list = (ITaskbarList2)new TaskbarListCoClass();
                list.HrInit();
                var me = Handle;
                if (me != IntPtr.Zero) list.MarkFullscreenWindow(me, on);
                foreach (Window w in OwnedWindows)
                {
                    var h = new WindowInteropHelper(w).Handle;
                    if (h != IntPtr.Zero) list.MarkFullscreenWindow(h, on);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.TraceWarning("taskbar fullscreen mark failed: " + ex.GetType().Name); }
        }

        [ComImport, Guid("602D4995-B13A-429b-A66E-1935E44F4317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList2
        {
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        }

        [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
        private class TaskbarListCoClass { }

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
        [DllImport("gdi32.dll")] private static extern bool GetDCOrgEx(IntPtr hdc, out POINT org);
        [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref POINT p);
        internal static readonly IntPtr BlackBrush = GetStockObject(4 /* BLACK_BRUSH */);

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_CTLCOLORSTATIC)
            {
                SetBkColor(wParam, 0x000000);
                handled = true;
                string org = "";
                if (_holdBrush != IntPtr.Zero)
                {
                    // The picture's top-left belongs at the host's top-left. GDI tiles a
                    // pattern brush from the DC's brush origin, and the DC the host paints
                    // with does not always start at the host's corner: CP2 round 5 run
                    // 41114710721 read the screen and saw the library picture shifted up by
                    // the host's offset in the window (7 of 24 Backs). The brush origin is
                    // set so the pattern starts exactly at the host's client corner.
                    var p0 = new POINT(); ClientToScreen(lParam, ref p0);
                    if (GetDCOrgEx(wParam, out var dcOrg))
                    {
                        int dx = p0.X - dcOrg.X, dy = p0.Y - dcOrg.Y;
                        SetBrushOrgEx(wParam, dx, dy, IntPtr.Zero);
                        org = $" dc-offset={dx},{dy}";
                    }
                }
                if (SwitchTrace.On) SwitchTrace.Mark("host.paint", _holdBrush != IntPtr.Zero ? "library-picture" + org : "black");
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
