using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace ZephyrPlayer.Tests
{
    /// <summary>
    /// Wake-only first press and blocked-input state. Mocks prove the rules and the routing, not
    /// real display or SendInput behavior: those need a real PC.
    /// </summary>
    public class DisplayGateTests
    {
        private sealed class Clock { public DateTime Now = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc); }

        private static (DisplayGate gate, Func<bool> dark, Action<bool> setDark, Func<int> wakes, Clock clock) Make(bool dark = true)
        {
            var d = dark; var w = 0; var clock = new Clock();
            var gate = new DisplayGate(() => d, () => w++, () => clock.Now);
            return (gate, () => d, v => d = v, () => w, clock);
        }

        [Fact]
        public void DarkDiscrete_FirstPressWakesOnly_NextPressWorks()
        {
            var (gate, _, setDark, wakes, _) = Make(dark: true);
            Assert.True(gate.ShouldSwallowDiscrete());
            Assert.Equal(1, wakes());
            setDark(false); // a real display-on notification arrived
            Assert.False(gate.ShouldSwallowDiscrete());
            Assert.Equal(1, wakes());
        }

        [Fact]
        public void StillDarkAfterWake_NextPressWakesAgain_NeverFiresBlind()
        {
            var (gate, _, _, wakes, _) = Make(dark: true);
            Assert.True(gate.ShouldSwallowDiscrete());
            Assert.True(gate.ShouldSwallowDiscrete());
            Assert.Equal(2, wakes());
        }

        [Fact]
        public void Continuous_GestureEatenToItsEnd_QuietReturnsControl()
        {
            var (gate, _, setDark, _, clock) = Make(dark: true);
            Assert.True(gate.ShouldSwallowContinuous());
            setDark(false);
            clock.Now += TimeSpan.FromMilliseconds(300);
            Assert.True(gate.ShouldSwallowContinuous());          // still inside the gesture
            clock.Now += TimeSpan.FromMilliseconds(300);
            Assert.True(gate.ShouldSwallowContinuous());          // each swallowed frame extends it
            clock.Now += DisplayGate.GestureQuiet + TimeSpan.FromMilliseconds(1);
            Assert.False(gate.ShouldSwallowContinuous());         // quiet hands: control is back
        }

        [Fact]
        public void Button_SwallowedPressSwallowsItsOwnRelease_OnlyForThatPhoneAndButton()
        {
            var (gate, _, setDark, _, _) = Make(dark: true);
            Assert.True(gate.ShouldSwallowButton("A", "right", true));
            Assert.Equal(1, gate.SwallowedButtonCountForTests);
            setDark(false);
            Assert.False(gate.ShouldSwallowButton("B", "right", false)); // another phone's release passes
            Assert.False(gate.ShouldSwallowButton("A", "left", false));  // another button's release passes
            Assert.True(gate.ShouldSwallowButton("A", "right", false));  // its own release is eaten, once
            Assert.False(gate.ShouldSwallowButton("A", "right", false));
            Assert.Equal(0, gate.SwallowedButtonCountForTests);
        }

        [Fact]
        public void Button_ReleaseOfPressDeliveredBeforeDark_AlwaysPasses()
        {
            var (gate, _, setDark, _, _) = Make(dark: false);
            Assert.False(gate.ShouldSwallowButton("A", "left", true));
            setDark(true);
            Assert.False(gate.ShouldSwallowButton("A", "left", false));
        }

        [Fact]
        public void Button_DeliveredDownThenDarkThenSwallowedSecondDown_StillReleasesOsButton()
        {
            var (gate, _, setDark, _, _) = Make(dark: false);
            Assert.False(gate.ShouldSwallowButton("A", "left", true));   // delivered
            setDark(true);
            Assert.True(gate.ShouldSwallowButton("A", "left", true));    // duplicate down swallowed
            Assert.False(gate.ShouldSwallowButton("A", "left", false));  // the real up must pass: OS button was down
            Assert.Equal(0, gate.SwallowedButtonCountForTests);
            Assert.True(gate.ShouldSwallowButton("A", "left", true));
            Assert.True(gate.ShouldSwallowButton("A", "left", false));   // its own up is eaten
            gate.ForgetOwner("A");
            Assert.Equal(0, gate.SwallowedButtonCountForTests);
            Assert.True(gate.ShouldSwallowButton("B", "left", true));
            Assert.True(gate.ShouldSwallowButton("C", "right", true));
            Assert.Equal(2, gate.SwallowedButtonCountForTests);
            gate.Prune(t => t == "C");                                   // B was revoked
            Assert.Equal(1, gate.SwallowedButtonCountForTests);
        }

        [Fact]
        public void PairedStore_IsPaired_DoesNotMarkPhoneAsSeen_ValidateAndTouchDoes()
        {
            var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zp-ispaired-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                var store = new PairedStore(System.IO.Path.Combine(dir, "paired.json"));
                store.Add("tok-1", "Phone");
                Assert.True(store.IsPaired("tok-1"));
                Assert.False(store.IsPaired("other"));
                Assert.Null(store.LastSeen("tok-1"));          // housekeeping must not look like a connection
                Assert.False(store.IsConnected("tok-1"));
                Assert.True(store.ValidateAndTouch("tok-1"));
                Assert.NotNull(store.LastSeen("tok-1"));
                store.Remove("tok-1");
                Assert.False(store.IsPaired("tok-1"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }
        }

        [Fact]
        public void DisplayStateMapping_OnlyOneMeansOn()
        {
            Assert.Equal(DisplayWatch.MonitorState.Off, DisplayWatch.Map(0));
            Assert.Equal(DisplayWatch.MonitorState.On, DisplayWatch.Map(1));
            Assert.Equal(DisplayWatch.MonitorState.Dimmed, DisplayWatch.Map(2));
            Assert.Equal(DisplayWatch.MonitorState.Unknown, DisplayWatch.Map(7));
            var w = new DisplayWatch();
            Assert.False(w.DarkOrUnknown);       // no notification yet fails open (same as 3.0.0)
            w.NoteRaw(0); Assert.True(w.DarkOrUnknown);
            w.NoteRaw(1); Assert.False(w.DarkOrUnknown);
            w.NoteRaw(2); Assert.True(w.DarkOrUnknown);
        }

        [Fact]
        public void InputBlocked_RaisedOnChangeOnly_ShortCountMeansBlocked()
        {
            InputSender.ResetBlockedForTests();
            var seen = new List<bool>();
            void H(bool b) => seen.Add(b);
            InputSender.BlockedStateChanged += H;
            try
            {
                InputSender.NoteSendResult(1, 1);
                InputSender.NoteSendResult(0, 1); InputSender.NoteSendResult(0, 3);
                Assert.True(InputSender.IsBlocked);
                InputSender.NoteSendResult(1, 2);                  // 1 of 2 is still short
                Assert.True(InputSender.IsBlocked);
                InputSender.NoteSendResult(3, 3);
                Assert.False(InputSender.IsBlocked);
                Assert.Equal(new List<bool> { true, false }, seen);
            }
            finally { InputSender.BlockedStateChanged -= H; InputSender.ResetBlockedForTests(); }
        }

        [Fact]
        public void BalloonThrottle_FirstAlwaysFires_ThenOncePerMinute()
        {
            Assert.True(TrayService.BalloonDue(long.MinValue, 5));
            Assert.False(TrayService.BalloonDue(1000, 1000 + 60_000));
            Assert.True(TrayService.BalloonDue(1000, 1000 + 60_001));
        }

        // ---- real server routing with a dark/lit gate and a recording sink ----
        private const int TestPort = 47885;
        private const string Token = "test-pairing-token";

        private sealed class Rec : IInputSink, IPlaybackControl
        {
            public readonly List<string> Events = new();
            public void Move(float dx, float dy) => Events.Add("move");
            public void Button(string b, bool d) => Events.Add($"button {b} {d}");
            public void Scroll(float dy) => Events.Add("scroll");
            public void Zoom(float d) => Events.Add("zoom");
            public void TypeText(string t) => Events.Add("type");
            public void NamedKey(string k) => Events.Add($"key {k}");
            public void Play(string url, long? pos, string[] subs) => Events.Add("play");
            public void Pause() => Events.Add("pause");
            public void Resume() => Events.Add("resume");
            public void Seek(long pos) => Events.Add("seek");
            public void SetVolume(int v) => Events.Add("volume");
            public void Mute(bool m) => Events.Add("mute");
            public void SetSpu(int t) => Events.Add("spu");
            public void SetFullscreen(bool on) => Events.Add("fullscreen");
            public string StatusJson() => "{\"state\":\"Stopped\"}";
        }

        private static StringContent Json(string b) => new(b, Encoding.UTF8, "application/json");

        [Fact]
        public async Task Server_DarkScreen_InputAndControlOnlyWake_ThenLitScreenDelivers()
        {
            var rec = new Rec();
            var dark = true; var wakes = 0;
            var gate = new DisplayGate(() => dark, () => wakes++);
            using var server = new PlaybackServer((u, p, s) => Task.CompletedTask, t => t == Token,
                TestPort, true, input: rec, control: rec, display: gate);
            server.Start();
            using var c = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{TestPort}") };
            c.DefaultRequestHeaders.Add("Authorization", "Bearer " + Token);

            var key = await c.PostAsync("/input/key", Json("{\"key\":\"enter\"}"));
            Assert.Contains("\"woke\":true", await key.Content.ReadAsStringAsync());
            var pause = await c.PostAsync("/control/pause", new StringContent(""));
            Assert.Contains("\"woke\":true", await pause.Content.ReadAsStringAsync());
            Assert.Contains("\"dark\":true", await c.GetStringAsync("/control/screen-state"));
            Assert.Empty(rec.Events);
            Assert.Equal(2, wakes);

            // heartbeat is never gated and a bad key is still a 400, not a wake
            var hb = await c.PostAsync("/input/heartbeat", Json("{}"));
            Assert.Equal(System.Net.HttpStatusCode.OK, hb.StatusCode);

            var badKey = await c.PostAsync("/input/key", Json("{\"key\":\"nope\"}"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badKey.StatusCode);
            var badBtn = await c.PostAsync("/input/button", Json("{\"button\":\"middle\",\"down\":true}"));
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, badBtn.StatusCode);
            Assert.Equal(2, wakes);

            var wake = await c.PostAsync("/control/wake-display", new StringContent(""));
            Assert.Equal(System.Net.HttpStatusCode.OK, wake.StatusCode);
            Assert.Equal(3, wakes);

            dark = false;
            Assert.Contains("\"dark\":false", await c.GetStringAsync("/control/screen-state"));
            await c.PostAsync("/input/key", Json("{\"key\":\"enter\"}"));
            await c.PostAsync("/control/pause", new StringContent(""));
            Assert.Equal(new List<string> { "key enter", "pause" }, rec.Events);
        }
    }
}
