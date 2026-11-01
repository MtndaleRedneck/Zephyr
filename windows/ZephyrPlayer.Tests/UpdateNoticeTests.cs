using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>2.0.2 updater: the automatic check also runs while the app is up (every 6 h,
    /// and on the way back from the tray), a failed re-check keeps an offer already found,
    /// the library shows a small "Update available" notice that opens Settings > Updates,
    /// and the update's quit goes through the app's own Quit.</summary>
    public class UpdateNoticeTests
    {
        private static void Reset()
        {
            BackgroundUpdateCheck.ResetForTest();
            UpdateState.AvailableOffer = null;
            UpdateState.Outcome = null;
        }

        [Fact]
        public void BackgroundCheck_FindsAnUpdate_KeepsItThroughAFailedRecheck_AndClearsWhenCurrent()
        {
            Reset();
            try
            {
                var changes = 0;
                Action onChange = () => Interlocked.Increment(ref changes);
                UpdateState.Changed += onChange;
                try
                {
                    BackgroundUpdateCheck.CheckOverride = v =>
                    {
                        UpdateState.AvailableOffer = new UpdateState.Offer("9.9.9", "https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v9.9.9/Zephyr-PC-9.9.9.exe", "ab");
                        return "available: 9.9.9";
                    };
                    Assert.True(BackgroundUpdateCheck.Run(first: true).Wait(10000));
                    Assert.Equal("available: 9.9.9", UpdateState.Outcome);
                    Assert.Equal("9.9.9", UpdateState.AvailableVersion);
                    Assert.True(changes >= 1);

                    // A later check that fails (network blip) keeps the known offer.
                    BackgroundUpdateCheck.CheckOverride = v => { UpdateState.AvailableOffer = null; throw new System.Net.Http.HttpRequestException("offline"); };
                    Assert.True(BackgroundUpdateCheck.Run().Wait(10000));
                    Assert.Equal("available: 9.9.9", UpdateState.Outcome);
                    Assert.Equal("9.9.9", UpdateState.AvailableOffer?.Version);

                    // Installed again up to date: the offer goes.
                    BackgroundUpdateCheck.CheckOverride = v => { UpdateState.AvailableOffer = null; return "up_to_date"; };
                    Assert.True(BackgroundUpdateCheck.Run().Wait(10000));
                    Assert.Equal("up_to_date", UpdateState.Outcome);
                    Assert.Null(UpdateState.AvailableVersion);

                    // A first-ever check that fails is recorded as failed, never as "no update".
                    Reset();
                    BackgroundUpdateCheck.CheckOverride = v => throw new System.Net.Http.HttpRequestException("offline");
                    Assert.True(BackgroundUpdateCheck.Run(first: true).Wait(10000));
                    Assert.Equal("failed: HttpRequestException", UpdateState.Outcome);
                }
                finally { UpdateState.Changed -= onChange; }
            }
            finally { Reset(); }
        }

        [Fact]
        public void BackgroundCheck_RunsEverySixHours_AndOnRestoreAtMostEveryHalfHour()
        {
            Reset();
            try
            {
                Assert.Equal(TimeSpan.FromHours(6), BackgroundUpdateCheck.Interval);
                Assert.True(BackgroundUpdateCheck.DueOnRestore(DateTime.UtcNow));
                BackgroundUpdateCheck.CheckOverride = v => "up_to_date";
                Assert.True(BackgroundUpdateCheck.Run().Wait(10000));
                Assert.Equal(1, BackgroundUpdateCheck.RunCount);
                Assert.False(BackgroundUpdateCheck.DueOnRestore(DateTime.UtcNow.AddMinutes(5)));
                Assert.True(BackgroundUpdateCheck.DueOnRestore(DateTime.UtcNow.AddMinutes(31)));
            }
            finally { Reset(); }
        }

        [Fact]
        public void UpdateQuit_GoesThroughTheAppsQuit()
        {
            var old = SelfUpdate.Quit;
            try
            {
                var quits = 0;
                SelfUpdate.Quit = () => quits++;
                SelfUpdate.RequestQuit();
                Assert.Equal(1, quits);
            }
            finally { SelfUpdate.Quit = old; }
        }

        [Fact]
        public void Library_ShowsUpdateAvailable_OnlyWhileAnUpdateIsKnown_AndItOpensSettingsUpdates()
        {
            Exception? error = null;
            var t = new Thread(() =>
            {
                var dir = Path.Combine(Path.GetTempPath(), "zephyr-202-updnotice-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    Reset();
                    var settings = new PlayerSettings(Path.Combine(dir, "settings.json"));
                    var store = new PairedStore(Path.Combine(dir, "paired.json"));
                    string? opened = null;
                    var home = new HomeView(settings, store, _ => { }, () => { }, loadDurations: false, openSettings: s => opened = s);
                    var window = new Window { Content = home, Width = 1100, Height = 700, Left = -20000, Top = -20000, ShowInTaskbar = false };
                    window.Show();
                    try
                    {
                        Pump(200);
                        Assert.False(home.IsUpdateNoticeShown, "the notice shows with no update known");
                        UpdateState.AvailableOffer = new UpdateState.Offer("2.0.9", "https://github.com/MtndaleRedneck/Zephyr/releases/download/player-v2.0.9/Zephyr-PC-2.0.9.exe", "ab");
                        UpdateState.Outcome = "available: 2.0.9";
                        PumpUntil(() => home.IsUpdateNoticeShown, "the notice did not follow the update check");
                        var button = (Button)home.FindName("UpdateNoticeButton")!;
                        Assert.Equal("Update available", ((TextBlock)home.FindName("UpdateNoticeText")!).Text);
                        Assert.Equal("Zephyr Player 2.0.9 is ready to install", button.ToolTip);
                        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
                        Assert.Equal("updates", opened);
                        UpdateState.AvailableOffer = null;
                        UpdateState.Outcome = "up_to_date";
                        PumpUntil(() => !home.IsUpdateNoticeShown, "the notice stayed after the update was gone");
                        UpdateState.Outcome = "failed: HttpRequestException";
                        Pump(200);
                        Assert.False(home.IsUpdateNoticeShown, "a failed check showed the notice");
                    }
                    finally { window.Close(); }
                }
                catch (Exception ex) { error = ex; }
                finally
                {
                    Reset();
                    try { Directory.Delete(dir, true); } catch { }
                    Dispatcher.CurrentDispatcher.InvokeShutdown();
                }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(60000), "STA body did not finish");
            if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        private static void Pump(int ms)
        {
            var until = Environment.TickCount64 + ms;
            do
            {
                var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
                Dispatcher.PushFrame(frame);
                Thread.Sleep(15);
            } while (Environment.TickCount64 < until);
        }

        private static void PumpUntil(Func<bool> test, string message, int ms = 5000)
        {
            var until = Environment.TickCount64 + ms;
            while (!test() && Environment.TickCount64 < until) Pump(30);
            Assert.True(test(), message);
        }
    }
}
