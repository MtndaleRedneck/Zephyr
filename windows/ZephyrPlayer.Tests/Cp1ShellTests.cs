using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Checkpoint 1 seams: one ShellWindow hosting the library and player views,
    /// the overlay leaving with the player view, bar controls that never take
    /// keyboard focus, one key map (Up/Down volume), local sidecar subtitles with
    /// libVLC's "Disable" (-1) entry filtered, remembered shell geometry, and the
    /// render-01 library composition rendered at 1600x1000.
    /// </summary>
    public class Cp1ShellTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();

        private static void Sta(Action body, int timeoutMs = 60000)
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                try { body(); }
                catch (Exception e) { error = e; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(timeoutMs), "STA body did not finish");
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        private static void Pump(int ms = 0)
        {
            var until = Environment.TickCount64 + ms;
            do
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                if (ms > 0) Thread.Sleep(15);
            } while (Environment.TickCount64 < until);
        }

        private static void PumpUntil(Func<bool> test, string message, int ms = 8000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until) Pump(30);
            Assert.True(test(), message);
        }

        private static string TempSettings() => Path.Combine(Path.GetTempPath(), "zephyr-cp1-" + Guid.NewGuid().ToString("N") + ".json");

        // ---------- keyboard (P0-4) ----------

        [Fact]
        public void BarControls_NeverTakeKeyboardFocus()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new MediaPlayer(libvlc);
                var view = new PlayerView(libvlc, player, new PlayerSettings(TempSettings()));
                var overlay = (FrameworkElement)view.FindName("Overlay")!;
                var found = 0;
                void Walk(object node)
                {
                    if (node is ButtonBase or Slider)
                    {
                        found++;
                        var fe = (FrameworkElement)node;
                        Assert.False(fe.Focusable, (fe.Name.Length > 0 ? fe.Name : fe.GetType().Name) + " can take keyboard focus: a click would strand the key map");
                    }
                    if (node is DependencyObject d)
                        foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
                }
                Walk(overlay);
                Assert.True(found >= 12, "expected every bar button and slider, found " + found);
                view.ReleasePlayback();
            });
        }

        [Fact]
        public void KeyMap_UpDownVolume_Mute_AndUnknownKeysPassThrough()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC();
                using var player = new MediaPlayer(libvlc);
                var view = new PlayerView(libvlc, player, new PlayerSettings(TempSettings()));
                var host = new Window { Content = view, Width = 800, Height = 450, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                host.Show();
                try
                {
                    var source = PresentationSource.FromVisual(host)!;
                    KeyEventArgs Key(Key k) => new(Keyboard.PrimaryDevice, source, 0, k) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                    view.SetVolumeTied(50);
                    var up = Key(System.Windows.Input.Key.Up); view.HandleKey(up);
                    Assert.True(up.Handled);
                    Assert.Equal(55, view.SharedVolume);
                    var down = Key(System.Windows.Input.Key.Down); view.HandleKey(down);
                    view.HandleKey(Key(System.Windows.Input.Key.Down));
                    Assert.Equal(45, view.SharedVolume);
                    Assert.Equal("45%", ((TextBlock)view.FindName("VolumeLabel")!).Text);
                    var m = Key(System.Windows.Input.Key.M); view.HandleKey(m);
                    Assert.True(view.SharedMute);
                    view.HandleKey(Key(System.Windows.Input.Key.M));
                    Assert.False(view.SharedMute);
                    var space = Key(System.Windows.Input.Key.Space); view.HandleKey(space);
                    Assert.True(space.Handled); // Space is the player's, never a focused button's "click"
                    var other = Key(System.Windows.Input.Key.A); view.HandleKey(other);
                    Assert.False(other.Handled);
                    var esc = Key(System.Windows.Input.Key.Escape); view.HandleKey(esc);
                    Assert.False(esc.Handled); // Esc only means something in full screen
                }
                finally { view.ReleasePlayback(); host.Close(); }
            });
        }

        // ---------- one shell, overlay leaves with the player (P0-1, P0-2) ----------

        [Fact]
        public void Shell_SwapsViewsInPlace_SameHandle_OverlayHiddenAfterLeavingPlayer()
        {
            Sta(() =>
            {
                Core.Initialize();
                using var libvlc = new LibVLC("--no-video", "--aout=dummy");
                using var player = new MediaPlayer(libvlc);
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-shell-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                var store = new PairedStore(Path.Combine(dir, "paired.json"));
                var shell = new ShellWindow(settings);
                var home = new HomeView(settings, store, _ => { }, () => { }, loadDurations: false);
                var view = new PlayerView(libvlc, player, settings);
                view.FullscreenChanged += on => shell.ApplyFullscreen(on);
                shell.ShowView(home);
                shell.Show();
                Pump(300);
                try
                {
                    var handle = shell.Handle;
                    var rect = new Rect(shell.Left, shell.Top, shell.ActualWidth, shell.ActualHeight);
                    Assert.NotEqual(IntPtr.Zero, handle);
                    Assert.Same(shell, Window.GetWindow(home));

                    shell.ShowView(view);
                    PumpUntil(() => view.IsLoaded, "player view never loaded in the shell");
                    var overlayWindow = Window.GetWindow((FrameworkElement)view.FindName("Overlay")!);
                    Assert.NotNull(overlayWindow);
                    Assert.NotSame(shell, overlayWindow); // LibVLCSharp's own overlay window (legitimate while playing)
                    PumpUntil(() => overlayWindow!.IsVisible, "overlay never shown with the player view");
                    Assert.Equal(handle, shell.Handle);
                    Assert.Equal(rect, new Rect(shell.Left, shell.Top, shell.ActualWidth, shell.ActualHeight));
                    Assert.False(home.IsLoaded); // swapped out, not hidden behind

                    // Library again (Back): same handle and geometry, overlay gone.
                    view.HideOverlayWindow();
                    shell.ShowView(home);
                    PumpUntil(() => !view.IsLoaded, "player view still in the tree after leaving");
                    Assert.False(overlayWindow!.IsVisible, "overlay window still visible after leaving the player");
                    Assert.Equal(handle, shell.Handle);
                    Assert.Equal(rect, new Rect(shell.Left, shell.Top, shell.ActualWidth, shell.ActualHeight));

                    // Player again, then tray close (App.CloseToTray order): overlay hidden, shell hidden.
                    shell.ShowView(view);
                    PumpUntil(() => view.IsLoaded && overlayWindow.IsVisible, "overlay did not come back with the player view");
                    view.HideOverlayWindow();
                    shell.ShowView(home);
                    shell.SaveBounds();
                    shell.Hide();
                    Pump(300);
                    Assert.False(overlayWindow.IsVisible, "overlay survived the tray close");
                    Assert.False(shell.IsVisible);
                    Assert.Equal(handle, shell.Handle);
                    Assert.NotNull(ShellWindow.ParseBounds(settings.ShellBounds));

                    // Restore: same window, library view.
                    shell.Show();
                    Pump(200);
                    Assert.Equal(handle, shell.Handle);
                    Assert.Same(home, shell.CurrentView);
                    Assert.False(overlayWindow.IsVisible);

                    // Full screen is the shell's chrome: entering and leaving keeps the handle.
                    shell.ShowView(view);
                    PumpUntil(() => view.IsLoaded, "player view did not reload");
                    view.SetFullscreen(true);
                    Pump(200);
                    Assert.True(shell.IsFullscreen);
                    Assert.Equal(WindowState.Maximized, shell.WindowState);
                    view.SetFullscreen(false);
                    Pump(200);
                    Assert.False(shell.IsFullscreen);
                    Assert.Equal(handle, shell.Handle);
                }
                finally
                {
                    view.ReleasePlayback();
                    shell.Close();
                }
            }, 90000);
        }

        // ---------- remembered geometry ----------

        [Fact]
        public void ShellBounds_RoundTrip_AndOffscreenFallsBackToDefault()
        {
            var text = ShellWindow.FormatBounds(new Rect(100, 80, 1600, 1000), false);
            Assert.Equal("100,80,1600,1000,0", text);
            var parsed = ShellWindow.ParseBounds(text);
            Assert.NotNull(parsed);
            Assert.Equal(new Rect(100, 80, 1600, 1000), parsed!.Value.Bounds);
            Assert.False(parsed.Value.Maximized);
            Assert.Null(ShellWindow.ParseBounds("garbage"));
            Assert.Null(ShellWindow.ParseBounds("1,2,3,4,0")); // too small to be a real window
            var work = new Rect(0, 0, 1920, 1040);
            var screen = new Rect(0, 0, 1920, 1080);
            Assert.Equal(new Rect(160, 20, 1600, 1000), ShellWindow.ChooseBounds(null, work, screen));
            Assert.Equal(new Rect(100, 80, 1600, 1000), ShellWindow.ChooseBounds(new Rect(100, 80, 1600, 1000), work, screen));
            Assert.Equal(new Rect(160, 20, 1600, 1000), ShellWindow.ChooseBounds(new Rect(5000, 5000, 1600, 1000), work, screen)); // monitor gone
            var small = ShellWindow.ChooseBounds(null, new Rect(0, 0, 1024, 728), new Rect(0, 0, 1024, 768));
            Assert.True(small.Width <= 1024 && small.Height <= 728);
        }

        // ---------- library layout helpers (render 01) ----------

        [Fact]
        public void Library_Labels_And_Columns_Match_Render01()
        {
            var (cols, tile) = HomeView.TileLayout(1596);
            Assert.Equal(8, cols);
            Assert.InRange(tile, 165, 170);
            var (cols2, tile2) = HomeView.TileLayout(1000);
            Assert.InRange(tile2, 140, 200);
            Assert.True(cols2 >= 4);
            Assert.Equal("11 folders · 29 videos", HomeView.CountLabel(11, 29));
            Assert.Equal("1 folder · 1 video", HomeView.CountLabel(1, 1));
            Assert.Equal("Name · Folders first", HomeView.SortText("name", true));
            Assert.Equal("Date created · Mixed", HomeView.SortText("created", false));
            if (OperatingSystem.IsWindows())
            {
                Assert.Equal(new[] { "D:" }, HomeView.RootPrefix(@"D:\Movies"));
                Assert.Equal(new[] { "C:", "..." }, HomeView.RootPrefix(@"C:\Users\me\Videos\Movies"));
            }
        }

        // ---------- updater constants (item 94) ----------

        [Fact]
        public void Updater_PointsAtTheCanonicalZephyrRepo()
        {
            Assert.Equal("MtndaleRedneck/Zephyr", UpdateCheck.Repo);
            Assert.Equal("https://api.github.com/repos/MtndaleRedneck/Zephyr/releases?per_page=30", UpdateCheck.Api);
            Assert.Equal("https://github.com/MtndaleRedneck/Zephyr/releases", UpdateCheck.ReleasePage);
            Assert.Equal("https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.0/ZephyrPlayer-Setup-2.0.0.exe", UpdateCheck.ExpectedAssetUrl("player-v2.0.0", "ZephyrPlayer-Setup-2.0.0.exe"));
            Assert.DoesNotContain("Zephyr-Dev", UpdateCheck.Api + UpdateCheck.ReleasePage);
        }

        // ---------- subtitles (P0-3) ----------

        [Fact]
        public void LocalSidecars_ExactNameFirst_ThenLanguageVariants_NothingElse()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-subs-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var movie = Path.Combine(dir, "My Movie.mp4");
            File.WriteAllBytes(movie, new byte[] { 0 });
            foreach (var name in new[] { "My Movie.en.srt", "My Movie.vtt", "My Movie.srt", "Other.srt", "My Movie Extras.srt", "My Movie.txt" })
                File.WriteAllText(Path.Combine(dir, name), "1\n00:00:00,000 --> 00:00:01,000\nHi\n");
            var found = SafeMediaSource.LocalSidecars(new Uri(movie).AbsoluteUri).Select(u => Path.GetFileName(new Uri(u).LocalPath)).ToArray();
            Assert.Equal(new[] { "My Movie.srt", "My Movie.vtt", "My Movie.en.srt" }, found);
            Assert.Empty(SafeMediaSource.LocalSidecars("http://127.0.0.1:1/s/x/1"));
            try { Directory.Delete(dir, true); } catch { }
        }

        [Fact]
        public void LocalSidecar_RegistersRealTrack_DisableEntryFiltered_SelectWorks()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-sidecar-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var movie = Path.Combine(dir, "Clip.mp4");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"), movie);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.srt"), Path.Combine(dir, "Clip.srt"));
            Core.Initialize();
            using var libvlc = new LibVLC("--no-video", "--aout=dummy");
            using var player = new MediaPlayer(libvlc);
            var url = new Uri(movie).AbsoluteUri;
            using var safe = new SafeMediaSource(libvlc, url);
            foreach (var sub in SafeMediaSource.LocalSidecars(url)) safe.AddSubtitle(sub);
            Assert.True(player.Play(safe.Media), "libVLC refused the local fixture");
            var deadline = Environment.TickCount64 + 10000;
            while (PlayerView.RealTracks(player.SpuDescription).Length == 0 && Environment.TickCount64 < deadline) Thread.Sleep(50);
            var real = PlayerView.RealTracks(player.SpuDescription);
            Assert.NotEmpty(real);
            Assert.DoesNotContain(real, t => t.Id == -1);
            Console.WriteLine("CP1_SUBS raw=[" + string.Join(", ", (player.SpuDescription ?? Array.Empty<LibVLCSharp.Shared.Structures.TrackDescription>()).Select(t => t.Id + ":" + t.Name)) + "] spu=" + player.Spu);
            Assert.True(player.SetSpu(real[0].Id));
            deadline = Environment.TickCount64 + 5000;
            while (player.Spu != real[0].Id && Environment.TickCount64 < deadline) Thread.Sleep(50);
            Assert.Equal(real[0].Id, player.Spu);
            Assert.True(player.SetSpu(-1));
            deadline = Environment.TickCount64 + 5000;
            while (player.Spu != -1 && Environment.TickCount64 < deadline) Thread.Sleep(50);
            Assert.Equal(-1, player.Spu);
            player.Stop();
        }

        // ---------- render 01 composition at 1600x1000 (offscreen evidence) ----------

        private static BitmapSource Poster(Color a, Color b, string label)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new LinearGradientBrush(a, b, 35), null, new Rect(0, 0, 320, 200));
                dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), null, new Point(230, 70), 46, 46);
                var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 26, Brushes.White, 1.0);
                dc.DrawText(text, new Point(18, 150));
            }
            var rtb = new RenderTargetBitmap(320, 200, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(visual);
            rtb.Freeze();
            return rtb;
        }

        [Fact]
        public void Library_Render01_Composition_At1600x968()
        {
            Sta(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1-r01-" + Guid.NewGuid().ToString("N"));
                var lib = Path.Combine(dir, "Movies");
                Directory.CreateDirectory(lib);
                var folders = new[] { "Action", "Classics", "Comedy", "Horror", "Kids", "Noir", "Silent era", "Thrillers", "New downloads", ".Watched" };
                foreach (var f in folders)
                {
                    Directory.CreateDirectory(Path.Combine(lib, f));
                    if (f != "New downloads") for (var i = 0; i < 3 + f.Length % 5; i++) File.WriteAllBytes(Path.Combine(lib, f, $"movie {i}.mp4"), new byte[] { 0 });
                }
                var videos = new[] { "Tears of Steel.mkv", "His Girl Friday.mp4", "Charade.mp4", "Night of the Living Dead.mkv", "Sintel.mkv", "Big Buck Bunny.mp4", "Detour.mp4", "Metropolis.mkv", "The General.mp4", "Elephants Dream.mkv", "Cosmos Laundromat.mp4", "Agent 327.mkv", "Spring.mp4", "Caminandes.mkv" };
                foreach (var v in videos) File.WriteAllBytes(Path.Combine(lib, v), new byte[1024 * 3]);
                var settings = new PlayerSettings(Path.Combine(dir, "settings.json")) { LibraryFolder = lib };
                var store = new PairedStore(Path.Combine(dir, "paired.json"));
                // The library view at render 01's client size (1600 x 1000 less the 32 px
                // caption bar). Laid out standalone: a real window on a 1024x768 runner
                // cannot be 1600 px wide (the desktop capture shows the real window).
                const int W = 1600, H = 968;
                var home = new HomeView(settings, store, _ => { }, () => { }, loadDurations: false);
                var frame = new Grid { Width = W, Height = H, Background = Brushes.Black };
                frame.Children.Add(home);
                void Layout() { frame.Measure(new Size(W, H)); frame.Arrange(new Rect(0, 0, W, H)); frame.UpdateLayout(); }
                Layout();
                Pump(200);
                Layout();
                try
                {
                    var list = (ListBox)home.FindName("MovieList")!;
                    var palette = new[] { (Color.FromRgb(0x8A, 0x3B, 0x12), Color.FromRgb(0x1F, 0x1A, 0x3A)), (Color.FromRgb(0x23, 0x5C, 0x7A), Color.FromRgb(0x0E, 0x16, 0x22)), (Color.FromRgb(0x6B, 0x5B, 0x2A), Color.FromRgb(0x18, 0x14, 0x0C)), (Color.FromRgb(0x3E, 0x6B, 0x3A), Color.FromRgb(0x0C, 0x18, 0x10)), (Color.FromRgb(0x7A, 0x2A, 0x4A), Color.FromRgb(0x1A, 0x0E, 0x16)) };
                    var n = 0;
                    foreach (var item in list.Items.Cast<object>())
                    {
                        var type = item.GetType();
                        var name = (string)type.GetProperty("Name")!.GetValue(item)!;
                        var dirEntry = (bool)type.GetProperty("Dir")!.GetValue(item)!;
                        var (a, b) = palette[n++ % palette.Length];
                        if (dirEntry) { if (name != "New downloads") type.GetProperty("Peek")!.SetValue(item, Poster(a, b, name)); }
                        else
                        {
                            type.GetProperty("Thumb")!.SetValue(item, Poster(a, b, Path.GetFileNameWithoutExtension(name)));
                            var duration = (long)(600000 + n * 431000);
                            type.GetMethod("SetMetadata")!.Invoke(item, new object[] { n % 3 == 0 ? duration * 35 / 100 : 0L, duration });
                        }
                    }
                    Pump(300);
                    Layout();
                    Pump(300);
                    Layout();
                    var w = W; var h = H;
                    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
                    rtb.Render(frame);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(rtb));
                    using (var fs = File.Create(Path.Combine(OutDir, "cp1-home-render-1600x968.png"))) png.Save(fs);
                    var px = new byte[w * h * 4];
                    rtb.CopyPixels(new Int32Rect(0, 0, w, h), px, w * 4, 0);
                    Color At(int x, int y) { var i = (y * w + x) * 4; return Color.FromRgb(px[i + 2], px[i + 1], px[i]); }
                    bool Near(Color c, byte r, byte g, byte bb, int tol = 6) => Math.Abs(c.R - r) <= tol && Math.Abs(c.G - g) <= tol && Math.Abs(c.B - bb) <= tol;
                    Assert.True(Near(At(900, 26), 0x0A, 0x0A, 0x0C), "app bar is not #0A0A0C: " + At(900, 26));
                    Assert.True(Near(At(1500, 88), 0x06, 0x06, 0x07), "window background is not #060607: " + At(1500, 88));
                    // Selected "Movies" pill: 3 px #FF9500 underline at the app bar's bottom edge.
                    var underline = 0;
                    for (var y = 44; y < 54; y++) for (var x = 14; x < 140; x++) { var c = At(x, y); if (c.R > 230 && c.G > 120 && c.G < 170 && c.B < 40) underline++; }
                    Assert.True(underline > 120, "no orange underline under the selected Movies pill: " + underline);
                    // Folder tiles: orange outlines across the first grid row (8 columns at 1600 px).
                    var columnsWithOrange = 0;
                    for (var col = 0; col < 8; col++)
                    {
                        var x0 = 40 + col * 191; var hits = 0;
                        for (var y = 118; y < 258; y++) for (var x = x0; x < Math.Min(w, x0 + 168); x++) { var c = At(x, y); if (c.R > 230 && c.G > 120 && c.G < 170 && c.B < 40) hits++; }
                        if (hits > 150) columnsWithOrange++;
                    }
                    Assert.Equal(8, columnsWithOrange);
                    Assert.InRange(home.TileWidth, 160, 172);
                    Console.WriteLine($"CP1_RENDER01 underline={underline} orangeColumns={columnsWithOrange} items={list.Items.Count}");
                }
                finally { try { Directory.Delete(dir, true); } catch { } }
            }, 90000);
        }
    }
}
