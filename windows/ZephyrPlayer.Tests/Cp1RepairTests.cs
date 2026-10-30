using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Xunit;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// CP1 repair (Instinct review of 7621d337): Boost reaches the decoder live,
    /// a failed or superseded start never strands the loading panel, native start
    /// failure completes loading, and non-UTF-8 local subtitles decode as
    /// Windows-1252 while phone subtitles stay strict.
    /// </summary>
    public class Cp1RepairTests
    {
        private static string OutDir => Environment.GetEnvironmentVariable("RUNNER_TEMP") ?? Path.GetTempPath();
        private static string ClipUrl => new Uri(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4")).AbsoluteUri;

        private static void Note(string name, string text)
        {
            try { File.WriteAllText(Path.Combine(OutDir, "cp1-unit-" + name + ".txt"), text); } catch { }
        }

        /// <summary>A real PlayerView hosted in a plain window (as the shell hosts it)
        /// on its own STA thread, with headless libVLC.</summary>
        private static void WithPlayer(Action<PlayerView, MediaPlayer, PlayerSettings> body, Action<PlayerSettings>? seed = null)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var settingsPath = Path.Combine(Path.GetTempPath(), "zephyr-cp1r-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy", "--verbose=2");
                    using var player = new MediaPlayer(vlc);
                    var settings = new PlayerSettings(settingsPath);
                    seed?.Invoke(settings);
                    var view = new PlayerView(vlc, player, settings);
                    var host = new System.Windows.Window { Content = view, Width = 640, Height = 360, ShowInTaskbar = false };
                    host.Show();
                    try { body(view, player, settings); }
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
            Assert.True(thread.Join(60000), "STA body did not finish");
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

        // ---------- 1. Boost live ----------

        [Fact]
        public void BoostChange_WhilePlaying_ReachesDecoderGain_WithoutReopen()
        {
            WithPlayer((view, player, settings) =>
            {
                var start = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "local start did not complete");
                start.GetAwaiter().GetResult();
                PumpUntil(() => player.State == VLCState.Playing, "clip did not reach Playing");

                view.SetVolumeTied(50);                     // fixed slider value
                Assert.Equal(50, view.AppliedPlayerGain);   // boost 100 %: gain = slider
                var media = view.CurrentMedia;
                var generation = view.PlayGeneration;
                Assert.NotNull(media);

                settings.VolumeBoostMaxPct = 200;           // what SettingsWindow's Boost choice writes
                PumpUntil(() => view.AppliedPlayerGain == 100, "Boost 200 % did not reach the decoder gain");
                var applied200 = view.AppliedPlayerGain;
                var readback200 = player.Volume;
                settings.VolumeBoostMaxPct = 150;
                PumpUntil(() => view.AppliedPlayerGain == 75, "Boost 150 % did not reach the decoder gain");
                var readback150 = player.Volume;

                // No reopen: same media object, same start generation, slider untouched.
                Assert.Same(media, view.CurrentMedia);
                Assert.Equal(generation, view.PlayGeneration);
                Assert.Equal(50, view.SharedVolume);
                Assert.Equal(50, settings.LastVolume);
                // libVLC's own Volume read-back is not evidence on a headless runner: it
                // read 0 here with the dummy audio output (CI run 41019817391) and -1 in
                // the real-desktop capture with no audio device, whatever was applied.
                // It is recorded below, not asserted; the assertion is on the gain
                // PlayerView hands to MediaPlayer.Volume on the settings-change path.
                Note("boost-live", $"slider=50 boost100->gain 50; boost200->applied {applied200} libvlc_readback={readback200}; boost150->applied 75 libvlc_readback={readback150}; same media={ReferenceEquals(media, view.CurrentMedia)}; generation {generation}->{view.PlayGeneration}; state={player.State}");
            }, s => s.VolumeBoostMaxPct = 100);
        }

        // ---------- 2. failed / superseded / native-failed starts ----------

        [Fact]
        public void FailedPhoneStart_EndsLoading_AndShowsError()
        {
            using var phone = new RefusingServer();
            WithPlayer((view, player, _) =>
            {
                // The production phone path: /control/play (and /play through App)
                // reach PlayFromUrlAsync through the control's PlayAsync.
                var control = new MainWindowPlaybackControl(view, player);
                var start = control.PlayAsync(phone.Url, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "failed phone start never completed");
                Assert.True(start.IsFaulted, "the refused start must fault so the server answers an error");
                Assert.IsNotType<OperationCanceledException>(start.Exception!.GetBaseException());
                Assert.True(view.IsLoadingErrorShown, "loading panel must show the error surface, not a spinner");
                Assert.Equal(PlayerView.OpenFailedMessage, view.LoadingDetailText);
                Assert.NotEqual("Opening...", view.LoadingDetailText);
                Assert.Equal("preparation", view.LastStartFailure);
                Assert.NotEqual(VLCState.Playing, player.State);
                Assert.True(phone.Requests > 0, "the phone server was never asked");
                Note("failed-phone-start", $"requests={phone.Requests} faulted={start.IsFaulted} error={start.Exception!.GetBaseException().GetType().Name} loadingShown={view.IsLoadingShown} errorSurface={view.IsLoadingErrorShown} detail=[{view.LoadingDetailText}] why={view.LastStartFailure}");
            });
        }

        [Fact]
        public void SupersededStart_OldFailure_NeverTouchesNewerStart()
        {
            const string oldUrl = "http://127.0.0.1:9/s/old/1";
            using var gate = new ManualResetEventSlim(false);
            WithPlayer((view, player, _) =>
            {
                // The old request ignores cancellation and fails for real AFTER the
                // newer start is already playing.
                view.PrepareOverride = (url, subs, token) =>
                {
                    if (url != oldUrl) return view.PrepareSource(url, subs, token);
                    gate.Wait(20000);
                    throw new InvalidDataException("old phone request failed late");
                };
                var old = view.PlayFromUrlAsync(oldUrl, null, Array.Empty<string>());
                var ping = false; view.Dispatcher.BeginInvoke(new Action(() => ping = true));
                PumpUntil(() => ping, "preparation blocked the UI");
                var next = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => next.IsCompleted, "newer start did not complete");
                next.GetAwaiter().GetResult();
                PumpUntil(() => player.State == VLCState.Playing, "newer start did not play");
                PumpUntil(() => !view.IsLoadingShown, "newer start left loading up");
                player.SetPause(true);
                PumpUntil(() => player.State == VLCState.Paused, "newer start did not pause");
                var media = view.CurrentMedia; var generation = view.PlayGeneration; var title = view.CurrentMovieTitle;

                gate.Set();
                PumpUntil(() => old.IsCompleted, "old start never completed");
                Assert.True(old.IsFaulted);
                Assert.IsType<InvalidDataException>(old.Exception!.GetBaseException());
                // Give any stray dispatcher work a chance to run, then check nothing moved.
                var settle = Environment.TickCount64 + 500; PumpUntil(() => Environment.TickCount64 > settle, "settle");
                Assert.False(view.IsLoadingShown, "old failure re-opened the loading/error panel over the newer movie");
                Assert.Null(view.LastStartFailure);
                Assert.Equal("clip", title);
                Assert.Equal(title, view.CurrentMovieTitle);
                Assert.Same(media, view.CurrentMedia);
                Assert.Equal(generation, view.PlayGeneration);
                Assert.Equal(VLCState.Paused, player.State);
                Note("superseded-start", $"old={old.Exception!.GetBaseException().GetType().Name} loadingShown={view.IsLoadingShown} title=[{view.CurrentMovieTitle}] state={player.State} generation={view.PlayGeneration} sameMedia={ReferenceEquals(media, view.CurrentMedia)}");
            });
        }

        [Fact]
        public void NativeStartFailure_CompletesLoading_WithError()
        {
            // An mp4 header with no playable boxes: preparation accepts it (the
            // header sniff passes), the native decoder cannot start it.
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-native-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var broken = Path.Combine(dir, "broken.mp4");
            var bytes = new byte[256 * 1024];
            new Random(7).NextBytes(bytes);
            var header = new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 2, 0, (byte)'i', (byte)'s', (byte)'o', (byte)'m', (byte)'i', (byte)'s', (byte)'o', (byte)'2' };
            Buffer.BlockCopy(header, 0, bytes, 0, header.Length);
            File.WriteAllBytes(broken, bytes);
            try
            {
                WithPlayer((view, player, _) =>
                {
                    view.NativeStallWindow = TimeSpan.FromSeconds(4); // backstop if libVLC reports nothing
                    var start = view.PlayFromUrlAsync(new Uri(broken).AbsoluteUri, null, Array.Empty<string>());
                    PumpUntil(() => start.IsCompleted, "start did not complete");
                    PumpUntil(() => view.IsLoadingErrorShown, "native failure left the loading panel on its spinner", 15000);
                    Assert.Equal(PlayerView.NativeFailedMessage, view.LoadingDetailText);
                    Assert.StartsWith("native-", view.LastStartFailure);
                    Assert.NotEqual(VLCState.Playing, player.State);
                    Note("native-start-failure", $"why={view.LastStartFailure} detail=[{view.LoadingDetailText}] state={player.State} startStatus={start.Status}");
                });
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        // ---------- player switch: no light video host behind the movie ----------

        private delegate bool EnumChildProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc cb, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, int msg, IntPtr w, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out WinRect r);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")] private static extern uint GetBkColor(IntPtr dc);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetSysColor(int index);
        private struct WinRect { public int Left, Top, Right, Bottom; }

        [Fact]
        public void PlayerVideoHost_PaintsBlack_NotSystemGrey()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1r-host-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    Core.Initialize();
                    using var vlc = new LibVLC("--no-video", "--aout=dummy");
                    using var player = new MediaPlayer(vlc);
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var shell = new ShellWindow(settings);
                    var view = new PlayerView(vlc, player, settings);
                    shell.ShowView(view);
                    shell.Show();
                    try
                    {
                        PumpUntil(() => view.IsLoaded, "player view never loaded");
                        var settle = Environment.TickCount64 + 600; PumpUntil(() => Environment.TickCount64 > settle, "settle");
                        // LibVLCSharp's video host: a native "static" child of the shell.
                        var host = IntPtr.Zero;
                        EnumChildWindows(shell.Handle, (h, _) => { var sb = new StringBuilder(64); GetClassNameW(h, sb, 64); if (sb.ToString().Equals("Static", StringComparison.OrdinalIgnoreCase)) { host = h; return false; } return true; }, IntPtr.Zero);
                        Assert.NotEqual(IntPtr.Zero, host);
                        // The colour query the static control makes before it paints.
                        var dc = GetDC(host);
                        IntPtr brush; uint bk;
                        try { brush = SendMessageW(shell.Handle, 0x0138 /* WM_CTLCOLORSTATIC */, dc, host); bk = GetBkColor(dc); }
                        finally { ReleaseDC(host, dc); }
                        Assert.Equal(ShellWindow.BlackBrush, brush);
                        Assert.Equal(0u, bk);
                        // What is on screen in the middle of the video host (no media yet):
                        // never the system button-face grey. CLR_INVALID = no readable
                        // desktop. (Run 41024150781: 19 of 25 points #0C0C0C; 6 lighter
                        // points #8C-#CC were overlay content, not the grey #F0F0F0.)
                        GetWindowRect(host, out var r);
                        var screen = GetDC(IntPtr.Zero);
                        var samples = new System.Collections.Generic.List<string>(); var grey = 0; var dark = 0; var readable = 0;
                        var face = GetSysColor(15 /* COLOR_BTNFACE: what Windows painted the host before */);
                        int fr = (int)(face & 0xFF), fg = (int)((face >> 8) & 0xFF), fb = (int)((face >> 16) & 0xFF);
                        try
                        {
                            for (var i = 1; i <= 5; i++) for (var j = 1; j <= 5; j++)
                            {
                                var x = r.Left + (r.Right - r.Left) * (25 + 10 * i) / 100;
                                var y = r.Top + (r.Bottom - r.Top) * (25 + 10 * j) / 100;
                                var c = GetPixel(screen, x, y);
                                if (c == 0xFFFFFFFF) continue;
                                readable++;
                                int cr = (int)(c & 0xFF), cg = (int)((c >> 8) & 0xFF), cb = (int)((c >> 16) & 0xFF);
                                if (Math.Abs(cr - fr) <= 4 && Math.Abs(cg - fg) <= 4 && Math.Abs(cb - fb) <= 4) grey++;
                                if (cr < 0x30 && cg < 0x30 && cb < 0x30) dark++;
                                samples.Add($"#{cr:X2}{cg:X2}{cb:X2}");
                            }
                        }
                        finally { ReleaseDC(IntPtr.Zero, screen); }
                        Note("video-host-colour", $"host=0x{host.ToInt64():X} rect=({r.Left},{r.Top},{r.Right},{r.Bottom}) ctlcolor brush is BLACK_BRUSH={brush == ShellWindow.BlackBrush} bk=0x{bk:X6}; screen samples readable={readable}/25 system-grey(#{fr:X2}{fg:X2}{fb:X2})={grey} dark={dark}: {string.Join(" ", samples)}");
                        // Overlay content (glyphs, text) may sit over a few points; the
                        // host itself must read black, and never the system grey.
                        Assert.Equal(0, grey);
                        if (readable >= 20) Assert.True(dark * 2 > readable, $"video host not mostly dark: {dark}/{readable}");
                    }
                    finally { view.ReleasePlayback(); shell.Close(); }
                }
                catch (Exception ex) { failure = ex; }
                finally
                {
                    try { Directory.Delete(dir, true); } catch { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(60000), "STA body did not finish");
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        // ---------- 4. subtitle encodings ----------

        private static byte[] Cp1252Fixture => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "cp1252.srt"));
        private const string Cp1252Line = "Price \u20AC5, \u201Cquoted\u201D caf\u00E9 \u2013 it\u2019s fine";

        [Fact]
        public void LocalSubtitle_Windows1252_DecodesEuroAndCurlyQuotes()
        {
            var raw = Cp1252Fixture;
            Assert.Contains((byte)0x80, raw); // the byte Latin-1 turned into U+0080
            var text = SafeMediaSource.DecodeSubtitle(raw, localFile: true);
            Assert.Contains(Cp1252Line, text);
            Assert.DoesNotContain('\u0080', text);
            Assert.DoesNotContain('\u0093', text);
        }

        [Fact]
        public void LocalSubtitle_Windows1252_StagedFileIsCorrectUtf8()
        {
            var dir = Path.Combine(Path.GetTempPath(), "zephyr-cp1252-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var movie = Path.Combine(dir, "Movie.mp4");
                File.Copy(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"), movie);
                var srt = Path.Combine(dir, "Movie.srt");
                File.WriteAllBytes(srt, Cp1252Fixture);
                Core.Initialize();
                using var vlc = new LibVLC("--no-video", "--aout=dummy");
                using var safe = new SafeMediaSource(vlc, new Uri(movie).AbsoluteUri);
                var staged = safe.StageSubtitle(new Uri(srt).AbsoluteUri);
                var written = File.ReadAllText(staged, new UTF8Encoding(false, true));
                Assert.Contains(Cp1252Line, written);
                Note("cp1252-subtitle", "staged: " + written.Replace("\r", "").Replace("\n", " | "));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void PhoneSubtitle_NonUtf8_StaysRefused()
        {
            Assert.Throws<DecoderFallbackException>(() => SafeMediaSource.DecodeSubtitle(Cp1252Fixture, localFile: false));
        }

        [Fact]
        public void Subtitle_Utf8AndUtf16Boms_Decode()
        {
            const string line = "1\r\n00:00:00,000 --> 00:00:02,000\r\n\u20AC \u201Cok\u201D\r\n";
            var utf8 = Encoding.UTF8.GetBytes(line);
            var utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF }; utf8Bom = Concat(utf8Bom, utf8);
            var utf16le = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes(line));
            var utf16be = Concat(new byte[] { 0xFE, 0xFF }, Encoding.BigEndianUnicode.GetBytes(line));
            foreach (var local in new[] { true, false })
            {
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf8, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf8Bom, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf16le, local));
                Assert.Equal(line, SafeMediaSource.DecodeSubtitle(utf16be, local));
            }
        }

        private static byte[] Concat(byte[] a, byte[] b) { var r = new byte[a.Length + b.Length]; Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length); return r; }


        // ---------- CP1 repair round 2: generation-safe native callbacks ----------

        private static void PumpFor(int ms)
        {
            var until = Environment.TickCount64 + ms;
            PumpUntil(() => Environment.TickCount64 > until, "pump", ms + 2000);
        }

        /// <summary>Instinct review 2, item 1. A previous start's Playing, EncounteredError,
        /// EndReached and Buffering callbacks, delivered late (queued on the dispatcher by
        /// libVLC's thread) across a newer start - during its preparation AND after its
        /// handoff - never clear the newer spinner, never show an error for it, and never
        /// stop the newer media. The callbacks are the previous start's OWN attach-time
        /// handlers (RaiseNativeEvent invokes the delegates libVLC holds).</summary>
        [Fact]
        public void StalePreviousStart_NativeCallbacks_NeverTouchNewerStart()
        {
            using var slow = new DribbleServer(slowSeconds: 7);
            WithPlayer((view, player, _) =>
            {
                var first = view.PlayFromUrlAsync(ClipUrl, null, Array.Empty<string>());
                PumpUntil(() => first.IsCompleted, "first start did not complete");
                first.GetAwaiter().GetResult();
                PumpUntil(() => player.State == VLCState.Playing && !view.IsLoadingShown, "first start did not play");
                var old = view.CurrentNativeStart!;
                Assert.NotNull(old);
                Assert.False(old.Waiting);
                var oldMedia = view.CurrentMedia;

                // Newer start from a slow phone: Opening... while the old movie still runs.
                var next = view.PlayFromUrlAsync(slow.Url, null, Array.Empty<string>());
                Assert.True(view.IsLoadingShown);
                foreach (var kind in new[] { "playing", "error", "ended", "buffering" }) view.RaiseNativeEvent(old, kind, 100);
                PumpFor(600);
                var duringPrep = $"loading={view.IsLoadingShown} error={view.IsLoadingErrorShown} detail=[{view.LoadingDetailText}] failure={view.LastStartFailure ?? "none"} state={player.State} sameOldMedia={ReferenceEquals(oldMedia, view.CurrentMedia)}";
                Assert.True(view.IsLoadingShown, "old Playing cleared the newer start's spinner during preparation");
                Assert.False(view.IsLoadingErrorShown, "old error/end showed an error for the newer start");
                Assert.Equal("Opening...", view.LoadingDetailText);
                Assert.Null(view.LastStartFailure);

                // After the handoff: the newer media is libVLC's, buffering slowly.
                PumpUntil(() => !ReferenceEquals(view.CurrentNativeStart, old) && view.CurrentNativeStart != null, "newer start was never handed to libVLC", 20000);
                var newer = view.CurrentNativeStart!;
                var newerMedia = view.CurrentMedia;
                Assert.Same(newer.Source.Media, newerMedia);
                Assert.True(newer.Waiting);
                foreach (var kind in new[] { "playing", "error", "ended", "buffering" }) view.RaiseNativeEvent(old, kind, 100);
                PumpFor(600);
                var afterHandoff = $"loading={view.IsLoadingShown} error={view.IsLoadingErrorShown} failure={view.LastStartFailure ?? "none"} newerWaiting={newer.Waiting} sameNewerMedia={ReferenceEquals(newerMedia, view.CurrentMedia)} state={player.State}";
                Assert.True(newer.Waiting, "a stale callback completed the newer start");
                Assert.Same(newer, view.CurrentNativeStart);
                Assert.Same(newerMedia, view.CurrentMedia);
                Assert.True(view.IsLoadingShown, "old Playing cleared the newer start's spinner after the handoff");
                Assert.False(view.IsLoadingErrorShown);
                Assert.Null(view.LastStartFailure);

                // The newer start completes on its OWN Playing.
                PumpUntil(() => player.State == VLCState.Playing && !view.IsLoadingShown, "newer slow start never played", 40000);
                Assert.Null(view.LastStartFailure);
                Assert.Same(newerMedia, view.CurrentMedia);
                // And the old handlers stay inert after it played.
                foreach (var kind in new[] { "error", "ended" }) view.RaiseNativeEvent(old, kind);
                PumpFor(400);
                Assert.Equal(VLCState.Playing, player.State);
                Assert.Same(newerMedia, view.CurrentMedia);
                Assert.Null(view.LastStartFailure);
                Note("stale-native-callbacks", $"during preparation: {duringPrep}; after handoff: {afterHandoff}; final: state={player.State} loading={view.IsLoadingShown} failure={view.LastStartFailure ?? "none"} sameNewerMedia={ReferenceEquals(newerMedia, view.CurrentMedia)}");
            });
        }

        // ---------- CP1 repair round 2: start contract on real signals ----------

        /// <summary>Instinct review 2, item 2. A valid phone movie that dribbles in far
        /// longer than the stall window (and longer than the old blind 20 s cap's
        /// test value) keeps making progress and is NOT stopped: it plays.</summary>
        [Fact]
        public void SlowPhoneSource_KeepsDelivering_IsNotStopped_AndPlays()
        {
            using var slow = new DribbleServer(slowSeconds: 12);
            WithPlayer((view, player, _) =>
            {
                view.NativeStallWindow = TimeSpan.FromSeconds(2);
                view.NativeStallPoll = TimeSpan.FromMilliseconds(250);
                var t0 = Environment.TickCount64;
                var start = view.PlayFromUrlAsync(slow.Url, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "slow start never handed off", 30000);
                start.GetAwaiter().GetResult();
                var a = view.CurrentNativeStart!;
                PumpUntil(() => player.State == VLCState.Playing && !view.IsLoadingShown || view.IsLoadingErrorShown, "slow start neither played nor failed", 60000);
                var elapsed = Environment.TickCount64 - t0;
                Note("slow-source-plays", $"stallWindow=2s elapsedToLoadingEnd={elapsed} ms completedBy={a.CompletedBy} state={player.State} failure={view.LastStartFailure ?? "none"} progressMarks={a.ProgressMarks} lastProgress=[{a.LastProgress}] bytesDelivered={a.Source.BytesDelivered} served={slow.BytesServed} requests={slow.Requests}");
                Assert.False(view.IsLoadingErrorShown, "a slow but valid source was stopped: " + view.LastStartFailure);
                Assert.Null(view.LastStartFailure);
                Assert.Equal(VLCState.Playing, player.State);
                // CI run 41033075025: Playing after 9.9 s with a 2 s stall window (libVLC reports
                // Playing once the demuxer opens, before the dribble phase ended).
                Assert.True(elapsed > 3 * 2000, $"the source was not actually slow ({elapsed} ms)");
                Assert.True(a.ProgressMarks > 3);
            });
        }

        /// <summary>The backstop: a source that stops delivering entirely (connection held
        /// open, no bytes) fails after the stall window with the error surface.</summary>
        [Fact]
        public void NoProgress_ForStallWindow_FailsStart_WithError()
        {
            using var stalled = new DribbleServer(slowSeconds: 0, stallAfter: 8192);
            WithPlayer((view, player, _) =>
            {
                view.NativeStallWindow = TimeSpan.FromSeconds(2);
                view.NativeStallPoll = TimeSpan.FromMilliseconds(200);
                var t0 = Environment.TickCount64;
                var start = view.PlayFromUrlAsync(stalled.Url, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "stalled start never handed off", 20000);
                PumpUntil(() => view.IsLoadingErrorShown, "a source with no progress never failed", 20000);
                var elapsed = Environment.TickCount64 - t0;
                Note("no-progress-fails", $"stallWindow=2s failedAfter={elapsed} ms why={view.LastStartFailure} detail=[{view.LoadingDetailText}] state={player.State}");
                Assert.Equal(PlayerView.NativeFailedMessage, view.LoadingDetailText);
                Assert.StartsWith("native-", view.LastStartFailure);
                Assert.Null(view.CurrentMedia);
            });
        }

        /// <summary>libVLC 3 says Playing once its demuxer opens, before the first
        /// picture (CI run 41033075025: ~0.7 s of all-black video area on a dribbling
        /// phone). Playing alone keeps the spinner; buffering complete after Playing
        /// ends it.</summary>
        [Fact]
        public void Playing_AloneKeepsSpinner_UntilBufferingCompletes()
        {
            using var slow = new DribbleServer(slowSeconds: 15);
            WithPlayer((view, player, _) =>
            {
                var start = view.PlayFromUrlAsync(slow.Url, null, Array.Empty<string>());
                PumpUntil(() => start.IsCompleted, "slow start never handed off", 30000);
                start.GetAwaiter().GetResult();
                var a = view.CurrentNativeStart!;
                Assert.True(a.Waiting);
                view.RaiseNativeEvent(a, "playing");
                view.RaiseNativeEvent(a, "vout", 1);
                PumpFor(400);
                var afterPlaying = $"loading={view.IsLoadingShown} playingSeen={a.PlayingSeen} waiting={a.Waiting}";
                Assert.True(a.PlayingSeen);
                Assert.True(a.Waiting, "Playing (or a black video output) ended the start before any picture");
                Assert.True(view.IsLoadingShown, "Playing alone hid the spinner");
                view.RaiseNativeEvent(a, "buffering", 100);
                PumpFor(400);
                Assert.False(a.Waiting);
                Assert.Equal("buffered", a.CompletedBy);
                Assert.False(view.IsLoadingShown, "buffering complete after Playing did not end loading");
                Assert.Null(view.LastStartFailure);
                Note("playing-keeps-spinner", $"after Playing + video output: {afterPlaying}; after Buffering 100%: loading={view.IsLoadingShown} completedBy={a.CompletedBy}");
            });
        }

        [Fact]
        public void StallWindow_Default_Is45Seconds_ThreePhoneReadDeadlines()
        {
            Assert.Equal(TimeSpan.FromSeconds(45), PlayerView.DefaultNativeStallWindow);
        }

        /// <summary>A phone server with Range support that serves clip.mp4 slowly: for
        /// the first [slowSeconds] after its first request every response dribbles
        /// 256 bytes per 250 ms (a slow but live phone), then full speed. With
        /// [stallAfter], once that many bytes were served in all, every response sends
        /// its headers and one byte, then nothing more while holding its connection
        /// open (a phone that went quiet).</summary>
        private sealed class DribbleServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private readonly byte[] _file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "clip.mp4"));
            private readonly CancellationTokenSource _stop = new();
            private long _firstRequest = -1, _served; private int _requests;
            public long BytesServed => Interlocked.Read(ref _served);
            public int Requests => Volatile.Read(ref _requests);
            public string Url { get; }
            public DribbleServer(int slowSeconds, int stallAfter = -1)
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                Url = $"http://127.0.0.1:{port}/s/tok/1";
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _ = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await _listener.GetContextAsync(); } catch { break; }
                        _ = Task.Run(() => Serve(ctx, slowSeconds, stallAfter));
                    }
                });
            }
            private async Task Serve(HttpListenerContext ctx, int slowSeconds, int stallAfter)
            {
                Interlocked.Increment(ref _requests);
                Interlocked.CompareExchange(ref _firstRequest, Environment.TickCount64, -1);
                try
                {
                    long start = 0, end = _file.Length - 1;
                    var range = ctx.Request.Headers["Range"];
                    var m = range == null ? null : System.Text.RegularExpressions.Regex.Match(range, @"bytes=(\d+)-(\d*)");
                    if (m != null && m.Success) { start = long.Parse(m.Groups[1].Value); if (m.Groups[2].Value.Length > 0) end = Math.Min(end, long.Parse(m.Groups[2].Value)); }
                    if (start > end) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
                    ctx.Response.StatusCode = m != null && m.Success ? 206 : 200;
                    ctx.Response.ContentType = "video/mp4";
                    ctx.Response.Headers["Accept-Ranges"] = "bytes";
                    if (ctx.Response.StatusCode == 206) ctx.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{_file.Length}";
                    ctx.Response.ContentLength64 = end - start + 1;
                    ctx.Response.SendChunked = false;
                    var output = ctx.Response.OutputStream;
                    var pos = start; var sent = 0L;
                    while (pos <= end && !_stop.IsCancellationRequested)
                    {
                        if (stallAfter >= 0 && sent > 0 && Interlocked.Read(ref _served) >= stallAfter) { await Task.Delay(Timeout.Infinite, _stop.Token); break; }
                        var slowPhase = Environment.TickCount64 - Interlocked.Read(ref _firstRequest) < slowSeconds * 1000L;
                        var n = (int)Math.Min(end - pos + 1, slowPhase ? 256 : 64 * 1024);
                        if (stallAfter >= 0) n = (int)Math.Max(1, Math.Min(n, stallAfter - Interlocked.Read(ref _served)));
                        await output.WriteAsync(_file.AsMemory((int)pos, n), _stop.Token);
                        await output.FlushAsync(_stop.Token);
                        pos += n; sent += n; Interlocked.Add(ref _served, n);
                        if (slowPhase) await Task.Delay(250, _stop.Token);
                    }
                }
                catch { }
                finally { try { ctx.Response.Close(); } catch { } }
            }
            public void Dispose() { _stop.Cancel(); try { _listener.Close(); } catch { } }
        }

        /// <summary>A phone server that refuses every movie request (404).</summary>
        private sealed class RefusingServer : IDisposable
        {
            private readonly HttpListener _listener = new();
            private int _requests;
            public int Requests => Volatile.Read(ref _requests);
            public string Url { get; }
            public RefusingServer()
            {
                var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
                Url = $"http://127.0.0.1:{port}/s/tok/1";
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                _ = Task.Run(async () =>
                {
                    while (_listener.IsListening)
                    {
                        HttpListenerContext ctx;
                        try { ctx = await _listener.GetContextAsync(); } catch { break; }
                        Interlocked.Increment(ref _requests);
                        try { ctx.Response.StatusCode = 404; } catch { }
                        finally { try { ctx.Response.Close(); } catch { } }
                    }
                });
            }
            public void Dispose() => _listener.Close();
        }
    }
}
