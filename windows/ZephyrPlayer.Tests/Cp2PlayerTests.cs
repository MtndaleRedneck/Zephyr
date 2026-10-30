using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>CP2 (renders 02-04): pop-ups one at a time, the sleep timer pauses and
    /// stays, speed chips and the fine slider, the Opening reset (carry-forward a), and
    /// the error state's Try again.</summary>
    public class Cp2PlayerTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
        private static string ClipUrl => new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;
        private static void Note(string name, string text) { try { File.WriteAllText(Path.Combine(OutDir, "cp2-unit-" + name + ".txt"), text); } catch { } }

        private static void WithPlayer(Action<PlayerView, MediaPlayer, PlayerSettings, Window> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var settingsPath = Path.Combine(Path.GetTempPath(), "zephyr-cp2-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy");
                    using var player = new MediaPlayer(vlc);
                    var settings = new PlayerSettings(settingsPath);
                    var view = new PlayerView(vlc, player, settings);
                    var host = new Window { Content = view, Width = 1024, Height = 640, Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
                    host.Show();
                    try { body(view, player, settings, host); }
                    finally { view.StopPlaybackSource(); host.Close(); }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { File.Delete(settingsPath); } catch { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(90000), "STA body did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void PumpUntil(Func<bool> test, string message, int ms = 8000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until)
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(10);
            }
            Assert.True(test(), message);
        }
        private static void Pump(int ms) { var t = Environment.TickCount64 + ms; PumpUntil(() => Environment.TickCount64 >= t, "pump"); }
        private static void Click(PlayerView v, string name) { var b = (Button)v.FindName(name)!; b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b)); }
        private static T El<T>(PlayerView v, string name) where T : class => (T)v.FindName(name)!;

        // ---------- pop-ups: one at a time ----------

        [Fact]
        public void Popups_OpenOneAtATime_AndCloseOnToggleEscOutsideClick()
        {
            WithPlayer((view, player, settings, host) =>
            {
                Pump(200);
                Click(view, "SpeedButton");
                Assert.Equal("speed", view.OpenPopupName);
                Assert.Equal(Visibility.Visible, El<Border>(view, "SpeedPopup").Visibility);
                Click(view, "SleepButton"); // another opens: the first closes
                Assert.Equal("sleep", view.OpenPopupName);
                Assert.Equal(Visibility.Collapsed, El<Border>(view, "SpeedPopup").Visibility);
                Assert.Equal(Visibility.Visible, El<Border>(view, "SleepPopup").Visibility);
                Click(view, "CaptionsButton");
                Assert.Equal("subtitles", view.OpenPopupName);
                Assert.Equal(Visibility.Collapsed, El<Border>(view, "SleepPopup").Visibility);
                var visible = 0;
                foreach (var n in new[] { "SpeedPopup", "SleepPopup", "SubtitlesPopup" }) if (El<Border>(view, n).Visibility == Visibility.Visible) visible++;
                Assert.Equal(1, visible);
                // Placement: above the bar (20 px gap) and inside the view.
                Pump(100);
                var pop = El<Border>(view, "SubtitlesPopup");
                var overlay = El<Grid>(view, "Overlay");
                var popRect = pop.TransformToAncestor(overlay).TransformBounds(new Rect(0, 0, pop.ActualWidth, pop.ActualHeight));
                var barTop = view.ControlBar.TransformToAncestor(overlay).Transform(new Point(0, 0)).Y;
                Assert.True(popRect.Left >= 19.5 && popRect.Right <= overlay.ActualWidth - 19.5, "pop-up not inside the view: " + popRect);
                Assert.InRange(barTop - popRect.Bottom, 19, 21);
                Click(view, "CaptionsButton"); // same button again: closes
                Assert.Null(view.OpenPopupName);
                // Esc closes.
                Click(view, "SpeedButton");
                var source = PresentationSource.FromVisual(host)!;
                var esc = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                view.HandleKey(esc);
                Assert.True(esc.Handled);
                Assert.Null(view.OpenPopupName);
                // A click outside (the top overlay) closes; a click inside does not.
                Click(view, "SleepButton");
                var inside = El<StackPanel>(view, "SleepRows");
                inside.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Assert.Equal("sleep", view.OpenPopupName);
                var outside = El<TextBlock>(view, "MovieTitle");
                outside.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Assert.Null(view.OpenPopupName);
                Note("popups", $"one-at-a-time ok; subtitles pop-up at {popRect} with bar top {barTop}");
            });
        }

        // ---------- sleep timer: pause, stay ----------

        [Fact]
        public void SleepTimer_Badge_And_Header_Formats()
        {
            var t0 = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
            var s = new SleepTimerState();
            Assert.Equal("", s.Badge(t0));
            s.Set(30, t0);
            Assert.Equal("30m", s.Badge(t0));
            Assert.Equal("30:00 left", s.Header(t0));
            Assert.Equal("27m", s.Badge(t0.AddMinutes(2).AddSeconds(19)));
            Assert.Equal("27:41 left", s.Header(t0.AddMinutes(2).AddSeconds(19)));
            Assert.Equal("45s", s.Badge(t0.AddMinutes(29).AddSeconds(15)));
            Assert.False(s.IsDue(t0.AddMinutes(29)));
            Assert.True(s.IsDue(t0.AddMinutes(30)));
            s.Set(90, t0);
            Assert.Equal("1:30:00 left", s.Header(t0));
            s.Set(SleepTimerState.EndOfMovie, t0);
            Assert.Equal("End", s.Badge(t0));
            Assert.Equal("End of movie", s.Header(t0));
            Assert.False(s.IsDue(t0.AddYears(1)));
            s.Off();
            Assert.False(s.IsActive);
        }

        [Fact]
        public void SleepTimer_TimeUp_Pauses_AndStaysInThePlayer()
        {
            WithPlayer((view, player, settings, host) =>
            {
                var home = 0; view.HomeRequested += () => home++;
                var now = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
                view.Clock = () => now;
                var start = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "start did not complete");
                PumpUntil(() => player.State == VLCState.Playing, "clip did not play");
                var media = view.CurrentMedia;
                // Pick "15 minutes" in the pop-up (row 1).
                Click(view, "SleepButton");
                var row = (Button)El<StackPanel>(view, "SleepRows").Children[1];
                row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, row));
                Assert.Equal(15, view.Sleep.Minutes);
                Assert.Equal("15m", El<TextBlock>(view, "SleepLabel").Text);
                Assert.Equal(Visibility.Visible, El<TextBlock>(view, "SleepLabel").Visibility);
                now = now.AddMinutes(14).AddSeconds(30); view.SleepTick();
                Assert.Equal("30s", El<TextBlock>(view, "SleepLabel").Text);
                Assert.Equal(VLCState.Playing, player.State);
                now = now.AddSeconds(30); view.SleepTick();
                PumpUntil(() => player.State == VLCState.Paused, "time up did not pause: " + player.State);
                Assert.Equal(1, view.SleepPausedCount);
                Assert.Equal("timer", view.LastSleepFire);
                Assert.Same(media, view.CurrentMedia);       // not stopped, not reopened
                Assert.Same(view, host.Content);             // still in the player
                Assert.Equal(0, home);                       // nothing navigated
                Assert.False(view.Sleep.IsActive);           // back to Off
                Assert.Equal(Visibility.Collapsed, El<TextBlock>(view, "SleepLabel").Visibility);
                Assert.Equal(1.0, view.ControlBar.Opacity);  // bar up on the paused movie
                Note("sleep-timer", $"15 min timer at +15:00 -> state={player.State} sameMedia={ReferenceEquals(media, view.CurrentMedia)} home={home} paused={view.SleepPausedCount}");
            });
        }

        [Fact]
        public void SleepTimer_EndOfThisMovie_FiresOnEndReached_AndStays()
        {
            WithPlayer((view, player, settings, host) =>
            {
                var home = 0; view.HomeRequested += () => home++;
                var start = view.PlayFromUrlAsync(ClipUrl, 3, Array.Empty<string>()); // 6 s clip from 0:03
                PumpUntil(() => start.IsCompleted, "start did not complete");
                PumpUntil(() => player.State == VLCState.Playing, "clip did not play");
                Click(view, "SleepButton");
                var row = (Button)El<StackPanel>(view, "SleepRows").Children[6];
                Assert.Equal("End of this movie", row.Content);
                row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, row));
                Assert.True(view.Sleep.IsEndOfMovie);
                Assert.Equal("End", El<TextBlock>(view, "SleepLabel").Text);
                var media = view.CurrentMedia;
                PumpUntil(() => view.MediaEndedCount > 0, "EndReached never reached the view", 20000);
                PumpUntil(() => view.SleepPausedCount == 1, "end of movie did not fire the sleep timer");
                Assert.Equal("end", view.LastSleepFire);
                Assert.NotEqual(VLCState.Stopped, player.State);
                Assert.Same(media, view.CurrentMedia);
                Assert.Same(view, host.Content);
                Assert.Equal(0, home);
                Assert.False(view.Sleep.IsActive);
                Assert.Equal(1.0, view.ControlBar.Opacity);
                Note("sleep-end-of-movie", $"EndReached count={view.MediaEndedCount} fired={view.LastSleepFire} state={player.State} home={home}");
            });
        }

        // ---------- speed ----------

        [Fact]
        public void Speed_Values_Format_And_Snap()
        {
            Assert.Equal(new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 }, PlayerBarLogic.SpeedChips);
            Assert.Equal("1.25\u00D7", PlayerBarLogic.FormatRate(1.25));
            Assert.Equal("2\u00D7", PlayerBarLogic.FormatRate(2));
            Assert.Equal("0.5\u00D7", PlayerBarLogic.FormatRate(0.5));
            Assert.Equal("Normal", PlayerBarLogic.ChipLabel(1));
            Assert.Equal(0.25, PlayerBarLogic.SnapRate(0.1));
            Assert.Equal(4.0, PlayerBarLogic.SnapRate(9));
            Assert.Equal(1.25, PlayerBarLogic.SnapRate(1.234));
            Assert.Equal(1.35, PlayerBarLogic.SnapRate(1.37));
            Assert.Equal(-1, PlayerBarLogic.ChipIndex(1.35));
            Assert.Equal(3, PlayerBarLogic.ChipIndex(1.25));
        }

        [Fact]
        public void Speed_Chips_And_FineSlider_SetTheRealRate()
        {
            WithPlayer((view, player, settings, host) =>
            {
                var start = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted && player.State == VLCState.Playing, "clip did not play");
                Click(view, "SpeedButton");
                var chips = El<System.Windows.Controls.Primitives.UniformGrid>(view, "SpeedChips");
                var c125 = (Button)chips.Children[3];
                c125.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, c125));
                Assert.Equal(1.25, view.CurrentRate);
                PumpUntil(() => Math.Abs(player.Rate - 1.25f) < 0.01, "libVLC rate is " + player.Rate);
                Assert.Equal("on", c125.Tag);
                Assert.Equal("1.25\u00D7", El<TextBlock>(view, "SpeedLabel").Text);
                Assert.Equal(Visibility.Visible, El<TextBlock>(view, "SpeedLabel").Visibility);
                Assert.Equal(1.25, El<Slider>(view, "SpeedSlider").Value, 3);
                Assert.Equal("speed", view.OpenPopupName); // a chip keeps the pop-up open
                // Fine slider: 0.25-4 in 0.05 steps.
                var slider = El<Slider>(view, "SpeedSlider");
                Assert.Equal(0.25, slider.Minimum); Assert.Equal(4.0, slider.Maximum); Assert.Equal(0.05, slider.TickFrequency, 3);
                slider.Value = 1.37;
                Assert.Equal(1.35, view.CurrentRate, 3);
                PumpUntil(() => Math.Abs(player.Rate - 1.35f) < 0.01, "libVLC rate is " + player.Rate);
                foreach (Button b in chips.Children) Assert.Equal("", b.Tag);
                Assert.Equal("1.35\u00D7", El<TextBlock>(view, "SpeedLabel").Text);
                slider.Value = 4;
                Assert.Equal(4.0, view.CurrentRate);
                var normal = (Button)chips.Children[2];
                normal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, normal));
                Assert.Equal(1.0, view.CurrentRate);
                Assert.Equal(Visibility.Collapsed, El<TextBlock>(view, "SpeedLabel").Visibility);
                Note("speed", $"chip 1.25 -> {view.CurrentRate}; slider 1.37 -> 1.35; normal -> badge hidden; libVLC rate {player.Rate}");
            });
        }

        // ---------- Opening reset (carry-forward a) ----------

        [Fact]
        public void Opening_ResetsTimeTitle_AndNeverShowsAnOldFailure()
        {
            const string phoneUrl = "http://127.0.0.1:9/s/new/1";
            using var gate = new ManualResetEventSlim(false);
            WithPlayer((view, player, settings, host) =>
            {
                var calls = 0;
                view.PrepareOverride = (url, subs, token) =>
                {
                    Interlocked.Increment(ref calls);
                    if (url == "http://127.0.0.1:9/s/broken/1") throw new InvalidDataException("refused");
                    if (url == phoneUrl) { gate.Wait(20000); throw new InvalidDataException("released"); }
                    return view.PrepareSource(url, subs, token);
                };
                // 1. An old movie playing, with real time and duration on the bar.
                var first = view.PlayFromUrlAsync(ClipUrl, 2, Array.Empty<string>());
                PumpUntil(() => first.IsCompleted && player.State == VLCState.Playing && !view.IsLoadingShown, "clip did not play");
                PumpUntil(() => El<TextBlock>(view, "TotalText").Text == "0:06 total" && El<TextBlock>(view, "ElapsedText").Text != "0:00", "bar never showed the clip's time");
                var oldTitle = view.CurrentMovieTitle;
                // 2. A new phone start opens (held in preparation; the old movie keeps playing).
                var next = view.PlayFromUrlAsync(phoneUrl, null, Array.Empty<string>());
                Pump(1500); // the 500 ms clock ticks with the old movie still underneath
                Assert.True(view.IsOpening);
                Assert.True(view.IsLoadingShown);
                Assert.Equal("0:00", El<TextBlock>(view, "ElapsedText").Text);
                Assert.Equal("-0:00", El<TextBlock>(view, "RemainingText").Text);
                Assert.Equal("0:00 total", El<TextBlock>(view, "TotalText").Text);
                Assert.Equal(0, El<Slider>(view, "SeekBar").Value);
                Assert.Equal("Movie from your phone", view.CurrentMovieTitle);
                Assert.Equal("", El<TextBlock>(view, "MovieMeta").Text);
                var opening = $"old=[{oldTitle}] opening: elapsed={El<TextBlock>(view, "ElapsedText").Text} total=[{El<TextBlock>(view, "TotalText").Text}] title=[{view.CurrentMovieTitle}]";
                gate.Set();
                PumpUntil(() => next.IsCompleted, "held start never completed");
                // 3. A failed start shows the error state; the next start's Opening shows
                //    ITS title, never the failure text.
                var broken = view.PlayFromUrlAsync("http://127.0.0.1:9/s/broken/1", null, Array.Empty<string>());
                PumpUntil(() => broken.IsCompleted && view.IsLoadingErrorShown, "failure did not show the error state");
                Assert.Equal(PlayerView.OpenFailedMessage, view.CurrentMovieTitle); // phone status keeps CP1's behaviour
                Assert.NotEqual(PlayerView.OpenFailedMessage, El<TextBlock>(view, "MovieTitle").Text); // never written into the title
                gate.Reset();
                view.PrepareOverride = (url, subs, token) => { gate.Wait(20000); return view.PrepareSource(url, subs, token); };
                var local = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                Pump(300);
                Assert.False(view.IsLoadingErrorShown);
                Assert.Equal("clip", view.CurrentMovieTitle);
                Assert.Equal("clip", El<TextBlock>(view, "MovieTitle").Text);
                Assert.Equal("0:00 total", El<TextBlock>(view, "TotalText").Text);
                gate.Set();
                PumpUntil(() => local.IsCompleted && player.State == VLCState.Playing, "local start after failure did not play");
                Note("opening-reset", opening + $"; after failure the local Opening showed title=[clip] error=False");
            });
        }

        // ---------- error state: Try again ----------

        [Fact]
        public void ErrorState_TryAgain_ReRunsTheSameStart_BackGoesHome()
        {
            WithPlayer((view, player, settings, host) =>
            {
                var calls = new System.Collections.Generic.List<string>();
                view.PrepareOverride = (url, subs, token) =>
                {
                    lock (calls) calls.Add(url + "|" + subs.Length);
                    if (calls.Count == 1) throw new InvalidDataException("first attempt refused");
                    return view.PrepareSource(url, subs, token);
                };
                var first = view.PlayFromUrlAsync(ClipUrl, 2, Array.Empty<string>());
                PumpUntil(() => first.IsCompleted, "first start never completed");
                Assert.True(view.IsLoadingErrorShown);
                Assert.Equal(PlayerView.OpenFailedMessage, El<TextBlock>(view, "ErrorMessage").Text);
                Assert.Equal("clip", El<TextBlock>(view, "ErrorTitle").Text);
                Assert.Equal(Visibility.Collapsed, view.ControlBar.Visibility);
                Assert.True(El<Button>(view, "ErrorRetryButton").IsEnabled);
                Click(view, "ErrorRetryButton");
                PumpUntil(() => player.State == VLCState.Playing && !view.IsLoadingShown, "Try again did not play", 15000);
                Assert.Equal(2, calls.Count);
                Assert.Equal(calls[0], calls[1]);
                Assert.Equal(1, view.RetryCount);
                Assert.Equal(2, view.StartCount);
                Assert.Equal(2L, view.LastStartArgs!.Value.Position);
                Assert.False(view.IsLoadingErrorShown);
                Assert.Equal(Visibility.Visible, view.ControlBar.Visibility);
                PumpUntil(() => player.Time >= 1500, "retry did not start at 0:02: " + player.Time);
                // Back to library from the error state.
                var home = 0; view.HomeRequested += () => home++;
                view.PrepareOverride = (url, subs, token) => throw new InvalidDataException("refused");
                var again = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => again.IsCompleted && view.IsLoadingErrorShown, "second failure did not show the error state");
                Click(view, "ErrorBackButton");
                Assert.Equal(1, home);
                Note("error-try-again", $"calls=[{string.Join(", ", calls)}] retry={view.RetryCount} starts={view.StartCount} played={player.State} back->home={home}");
            });
        }

        // ---------- item 44: black caption over the player ----------

        [Fact]
        public void Caption_IsBlackOverThePlayer_LibraryKeepsItsOwn()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp2-caption-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy");
                    using var player = new MediaPlayer(vlc);
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var shell = new ShellWindow(settings);
                    var home = new HomeView(settings, new PairedStore(Path.Combine(dir, "paired.json")), _ => { }, () => { }, loadDurations: false);
                    var view = new PlayerView(vlc, player, settings);
                    var caption = (Border)shell.FindName("CaptionBar")!;
                    shell.ShowView(home);
                    Assert.Equal(System.Windows.Media.Color.FromRgb(0x0A, 0x0A, 0x0C), ((System.Windows.Media.SolidColorBrush)caption.Background).Color);
                    shell.ShowView(view);
                    Assert.Equal(System.Windows.Media.Colors.Black, ((System.Windows.Media.SolidColorBrush)caption.Background).Color);
                    shell.ShowView(home, () => view.StopPlaybackSource());
                    Assert.Equal(System.Windows.Media.Color.FromRgb(0x0A, 0x0A, 0x0C), ((System.Windows.Media.SolidColorBrush)caption.Background).Color);
                    view.ReleasePlayback();
                    shell.Close();
                }
                catch (Exception ex) { failure = ex; }
                finally { try { Directory.Delete(dir, true); } catch { } Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(60000), "STA body did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // ---------- pure layout logic ----------

        [Fact]
        public void FullscreenPill_780IsAMax_ClampedToTheScreen()
        {
            Assert.Equal(780, PlayerBarLogic.PillDrawnWidth(1920), 3);
            Assert.Equal(780, PlayerBarLogic.PillDrawnWidth(1024), 3);   // the 1024x768 runner
            Assert.Equal(752, PlayerBarLogic.PillDrawnWidth(800), 3);
            Assert.Equal(1000, PlayerBarLogic.PillLayoutWidth(1920), 3);
            Assert.True(PlayerBarLogic.PillLayoutWidth(640) * PlayerBarLogic.PillScale <= 640 - 48 + 0.001);
        }

        [Fact]
        public void Bar_Reflows_InSteps_AtNarrowWidths()
        {
            // centre cluster 302 px, right group 164 px (+ badges 50).
            Assert.Equal(0, PlayerBarLogic.ReflowLevel(1000, 302, 214, 164));
            Assert.Equal(1, PlayerBarLogic.ReflowLevel(302 + 2 * 170, 302, 164, 164));
            Assert.Equal(2, PlayerBarLogic.ReflowLevel(302 + 2 * 120, 302, 104, 104));
            Assert.Equal(3, PlayerBarLogic.ReflowLevel(302 + 2 * 60, 302, 50, 50));
            Assert.Equal(4, PlayerBarLogic.ReflowLevel(302 + 2 * 170, 302, 214, 164));
        }

        [Fact]
        public void MetaLine_And_Delay_Format()
        {
            Assert.Equal("2012 \u00B7 1h 42m", PlayerBarLogic.MetaLine("2012-05-01", 6128000));
            Assert.Equal("1h 42m", PlayerBarLogic.MetaLine(null, 6128000));
            Assert.Equal("42m", PlayerBarLogic.MetaLine("", 42 * 60000));
            Assert.Equal("6s", PlayerBarLogic.MetaLine("junk", 6000));
            Assert.Equal("+0.0 s", PlayerBarLogic.FormatDelay(0));
            Assert.Equal("+0.3 s", PlayerBarLogic.FormatDelay(300));
            Assert.Equal("-1.2 s", PlayerBarLogic.FormatDelay(-1200));
        }

        [Fact]
        public void LibVlc_Accepts_The_Subtitle_Look_Options()
        {
            Core.Initialize();
            foreach (var size in new[] { "S", "M", "L" })
            {
                var options = PlayerBarLogic.SubtitleOptions(size, 1.25);
                Assert.Contains("--sub-margin=188", options);
                using var vlc = new LibVLC(options);
                Assert.NotNull(vlc);
            }
            Assert.Contains("--sub-text-scale=62", PlayerBarLogic.SubtitleOptions("M", 1));
            Assert.Contains("--sub-margin=150", PlayerBarLogic.SubtitleOptions("M", 1));
            using var app = App.CreateLibVlc(new PlayerSettings(Path.Combine(Path.GetTempPath(), "zephyr-cp2-vlc-" + Guid.NewGuid().ToString("N") + ".json")));
            Assert.NotNull(app);
        }

        // ---------- CP2 repair round (Instinct CP2 review 1) ----------

        private static string ClipMkvUrl => new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mkv")).AbsoluteUri;

        /// <summary>The seek bar's own registered handlers, driven the way WPF drives them
        /// for a press and release on the track (OnSeekDown / OnSeekUp).</summary>
        private static void Press(Slider seek, RoutedEvent ev)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = ev, Source = seek };
            seek.RaiseEvent(args);
        }

        [Fact]
        public void Opening_HeldNewStart_NoSeekReachesTheOldMovie_AndTheZeroTimelineStays()
        {
            const string phoneUrl = "http://127.0.0.1:9/s/held/1";
            using var gate = new ManualResetEventSlim(false);
            WithPlayer((view, player, settings, host) =>
            {
                view.PrepareOverride = (url, subs, token) =>
                {
                    // The new start's preparation is HELD; once released it gets a real
                    // (second) fixture, so the same seek path can be checked afterwards.
                    if (url == phoneUrl) { gate.Wait(20000); return view.PrepareSource(ClipMkvUrl, subs, token); }
                    return view.PrepareSource(url, subs, token);
                };
                var seek = El<Slider>(view, "SeekBar");
                // 1. The OLD movie plays, with its own time and duration on the bar.
                var first = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => first.IsCompleted && player.State == VLCState.Playing && !view.IsLoadingShown, "old clip did not play");
                PumpUntil(() => El<TextBlock>(view, "TotalText").Text == "0:06 total", "bar never showed the old clip's duration");
                Assert.True(view.SeekBarEnabled);
                var oldMedia = view.CurrentMedia;
                // 2. A new phone start opens and is held in preparation: the old movie keeps
                //    playing underneath (it is only replaced at handoff).
                var next = view.PlayFromUrlAsync(phoneUrl, null, Array.Empty<string>());
                Pump(600);
                Assert.True(view.IsOpening);
                Assert.Equal(VLCState.Playing, player.State);
                Assert.Same(oldMedia, view.CurrentMedia);
                Assert.False(view.SeekBarEnabled); // real mouse input cannot reach the track
                var before = player.Time;
                // 3a. Press on the track, the thumb moved to 75 % (what IsMoveToPointEnabled
                //     does on a press), release: the real OnSeekDown / ValueChanged / OnSeekUp.
                Press(seek, UIElement.PreviewMouseDownEvent);
                seek.Value = 750;
                var elapsedWhileHeld = El<TextBlock>(view, "ElapsedText").Text;
                Press(seek, UIElement.PreviewMouseUpEvent);
                // 3b. A press-release with the thumb where it is (value 0).
                Press(seek, UIElement.PreviewMouseDownEvent);
                Press(seek, UIElement.PreviewMouseUpEvent);
                // 3c. The skip buttons (the Left/Right keys call the same handlers) and the
                //     phone's /control/seek.
                Click(view, "SkipForwardButton");
                Click(view, "SkipBackButton");
                Click(view, "SecondarySkipForwardButton");
                var control = new MainWindowPlaybackControl(view, player);
                control.Seek(5);
                Pump(400);
                var after = player.Time;
                Assert.Equal(-1, view.LastSeekFraction); // nothing was handed to libVLC
                Assert.True(after >= before - 250 && after <= before + 1500, $"the old movie was moved by a seek during Opening: {before} ms -> {after} ms");
                Assert.Equal(VLCState.Playing, player.State);
                Assert.Same(oldMedia, view.CurrentMedia);
                // The new start's title and zero timeline are intact.
                Assert.Equal("0:00", elapsedWhileHeld);
                Assert.Equal(0, seek.Value);
                Assert.Equal("0:00", El<TextBlock>(view, "ElapsedText").Text);
                Assert.Equal("-0:00", El<TextBlock>(view, "RemainingText").Text);
                Assert.Equal("0:00 total", El<TextBlock>(view, "TotalText").Text);
                Assert.Equal("Movie from your phone", view.CurrentMovieTitle);
                Assert.Equal("Movie from your phone", El<TextBlock>(view, "MovieTitle").Text);
                Assert.True(view.IsOpening);
                var during = $"opening held over the playing old clip: old time {before} ms -> {after} ms after a 75 % track press/release, a value-0 press/release, skip +10 s, skip -10 s, skip +30 s and phone seek 5 s; last_seek_fraction={view.LastSeekFraction}; seek bar enabled={view.SeekBarEnabled}; bar elapsed/remaining/total=[{El<TextBlock>(view, "ElapsedText").Text}] [{El<TextBlock>(view, "RemainingText").Text}] [{El<TextBlock>(view, "TotalText").Text}] value={seek.Value}; title=[{view.CurrentMovieTitle}]; old media still playing={ReferenceEquals(oldMedia, view.CurrentMedia) && player.State == VLCState.Playing}";
                // 4. Release the new start: it plays, the bar takes input again, and the SAME
                //    event path now seeks the new movie.
                gate.Set();
                PumpUntil(() => next.IsCompleted && player.State == VLCState.Playing && !view.IsOpening, "held start never played", 15000);
                Assert.NotSame(oldMedia, view.CurrentMedia);
                Assert.True(view.SeekBarEnabled);
                PumpUntil(() => El<TextBlock>(view, "TotalText").Text == "0:06 total", "bar never showed the new clip's duration");
                Press(seek, UIElement.PreviewMouseDownEvent);
                seek.Value = 750;
                Press(seek, UIElement.PreviewMouseUpEvent);
                Assert.Equal(0.75, view.LastSeekFraction, 3);
                PumpUntil(() => player.Time >= 4000, "the same seek path did not move the new movie (time " + player.Time + " ms)");
                Note("r2-opening-seek-guard", during + $"\nafter release: new movie playing, seek bar enabled={view.SeekBarEnabled}; the same press/75 %/release seeked it: last_seek_fraction={view.LastSeekFraction}, time {player.Time} ms of {player.Length} ms");
            });
        }

        [Fact]
        public void Speed_AfterASecondStart_DecoderRunsAtTheRateTheBarShows()
        {
            WithPlayer((view, player, settings, host) =>
            {
                double ClockRate(int ms)
                {
                    var t0 = player.Time; var w0 = Environment.TickCount64;
                    Pump(ms);
                    var t1 = player.Time; var w1 = Environment.TickCount64;
                    return (t1 - t0) / (double)Math.Max(1, w1 - w0);
                }
                var first = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => first.IsCompleted && player.State == VLCState.Playing && !view.IsOpening, "first clip did not play");
                var baseline = ClockRate(1200); // the decoder clock at 1x on this runner
                // 1.5x on the first movie, through the real chip.
                Click(view, "SpeedButton");
                var chips = El<System.Windows.Controls.Primitives.UniformGrid>(view, "SpeedChips");
                var c15 = (Button)chips.Children[4];
                c15.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, c15));
                Assert.Equal(1.5, view.CurrentRate);
                PumpUntil(() => Math.Abs(player.Rate - 1.5f) < 0.01, "libVLC rate is " + player.Rate);
                var reappliedBefore = view.RateReapplyCount;
                // A SECOND media start (a different file, the shared start path).
                var second = view.PlayFromUrlAsync(ClipMkvUrl, null, Array.Empty<string>());
                PumpUntil(() => second.IsCompleted && player.State == VLCState.Playing && !view.IsOpening, "second clip did not play");
                var atHandoff = view.RateAtHandoff;
                var reapplied = view.RateReapplyCount - reappliedBefore;
                var label = El<TextBlock>(view, "SpeedLabel").Text;
                var rateNow = player.Rate;
                var clock = ClockRate(1500);
                var ratio = clock / baseline;
                Note("r2-speed-second-start", $"first start 1x: decoder clock {baseline:0.000} media-ms per wall-ms. Chip 1.5x on the first movie -> libVLC rate {1.5f}. Second start (clip.mkv): libVLC rate read at handoff {atHandoff:0.###} (before any reapply); reapplied at handoff {reapplied} time(s); bar shows [{label}] CurrentRate {view.CurrentRate}; libVLC rate while playing {rateNow:0.###}; decoder clock {clock:0.000} = {ratio:0.00}x the 1x baseline.");
                Assert.Equal(1.5, view.CurrentRate);
                Assert.Equal(PlayerBarLogic.FormatRate(1.5), label);
                Assert.True(Math.Abs(rateNow - 1.5f) < 0.01, "libVLC rate after the second start is " + rateNow);
                Assert.True(ratio > 1.25, $"the decoder clock after the second start runs at {ratio:0.00}x the 1x baseline; the bar shows 1.5x");
            });
        }

        [Fact]
        public void SleepTimer_AcrossBackAndReturn_CurrentLifecycle()
        {
            WithPlayer((view, player, settings, host) =>
            {
                var t0 = DateTime.UtcNow; var now = t0;
                view.Clock = () => now;
                // "Back" and "tray close" reach the player view through App.LeavePlayer, whose
                // view-side calls are exactly these (tray close adds HideOverlayWindow again
                // and hides the shell).
                void Back() { view.SavePlaybackProgress(); view.StopPlaybackSource(); view.HideOverlayWindow(); view.HideOverlayWindow(); }
                void StartClip()
                {
                    var s = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                    PumpUntil(() => s.IsCompleted && player.State == VLCState.Playing && !view.IsOpening, "clip did not play");
                }
                var log = new System.Text.StringBuilder();
                // The 1-second DispatcherTimer normally runs SleepTick; if this test pump
                // does not get to it in 2.5 s, SleepTick is called directly (as the other
                // sleep tests do) and the log says so.
                string Due(int count)
                {
                    var until = Environment.TickCount64 + 2500;
                    while (view.SleepPausedCount < count && Environment.TickCount64 < until) Pump(50);
                    if (view.SleepPausedCount >= count) return "the running 1 s tick fired it";
                    view.SleepTick();
                    return "fired by a direct SleepTick() call";
                }
                // A. A 15-minute timer, Back at minute 2, a new movie at minute 10.
                StartClip();
                view.SetSleep(15);
                now = t0.AddMinutes(2);
                Back();
                log.AppendLine($"A. 15 min timer set, Back at +2:00: timer active={view.Sleep.IsActive} minutes={view.Sleep.Minutes} tick running={view.SleepTickRunning} remaining={view.Sleep.Remaining(now)}");
                Assert.True(view.Sleep.IsActive);
                Assert.Equal(15, view.Sleep.Minutes);
                Assert.True(view.SleepTickRunning);
                now = t0.AddMinutes(10);
                StartClip();
                Pump(1200);
                var badge = El<TextBlock>(view, "SleepLabel").Text;
                log.AppendLine($"   new movie started at +10:00: badge [{badge}] (the same deadline), paused count {view.SleepPausedCount}");
                Assert.Equal("5m", badge);
                now = t0.AddMinutes(15).AddSeconds(1);
                var howA = Due(1);
                Assert.Equal(1, view.SleepPausedCount);
                PumpUntil(() => player.State == VLCState.Paused, "the new movie was not paused: " + player.State);
                log.AppendLine($"   at +15:01 the carried timer fired on the NEW movie ({howA}): state={player.State} why={view.LastSleepFire} timer now active={view.Sleep.IsActive}");
                Assert.Equal("timer", view.LastSleepFire);
                Assert.False(view.Sleep.IsActive);
                // B. A timer that comes due while the library shows.
                player.SetPause(false);
                PumpUntil(() => player.State == VLCState.Playing, "clip did not resume");
                now = t0.AddMinutes(20);
                view.SetSleep(15);
                Back();
                now = t0.AddMinutes(36);
                var tickInLibrary = view.SleepTickRunning;
                var howB = Due(2);
                Assert.Equal(2, view.SleepPausedCount);
                Assert.True(tickInLibrary);
                log.AppendLine($"B. 15 min timer, Back, due at +35:00 while the library shows (tick running after Back={tickInLibrary}; {howB}): fired={view.LastSleepFire} with no media (player state {player.State}), timer now active={view.Sleep.IsActive}, tick running={view.SleepTickRunning}; nothing was playing to pause");
                Assert.False(view.Sleep.IsActive);
                Assert.False(view.SleepTickRunning);
                Assert.NotEqual(VLCState.Playing, player.State);
                // C. "End of this movie", Back, a new movie: it fires at THAT movie's end.
                StartClip();
                view.SetSleep(SleepTimerState.EndOfMovie);
                Back();
                Assert.True(view.Sleep.IsEndOfMovie);
                StartClip();
                PumpUntil(() => view.SleepPausedCount == 3, "end-of-movie timer did not fire on the next movie's end", 15000);
                log.AppendLine($"C. 'End of this movie', Back, new movie: timer still 'End' after Back; fired at the NEW movie's end ({view.LastSleepFire}); timer now active={view.Sleep.IsActive}");
                Assert.Equal("end", view.LastSleepFire);
                Note("r2-sleep-across-back", log.ToString());
            });
        }
    }
}
