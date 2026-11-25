using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Xunit;
using ZephyrVlcRemote;

namespace ZephyrVlcRemote.Tests;

/// <summary>Bounded wiring tests through the real HttpServer: a button held when the screen goes dark must still be
/// released over both HTTP and the live connection. InputSender.ButtonSink replaces the real mouse, so nothing is
/// injected on the runner. ProgramState runs alone, so port 8492 is not shared with the HTTP reservation tests.</summary>
[Collection("ProgramState")]
public class HeldButtonWiringTests : IDisposable
{
    private const string Token = "wiring-test-token";
    private const string TokenB = "wiring-test-token-b";
    private readonly List<(string Button, bool Down)> _sent = new();
    private readonly object _l = new();
    private readonly HttpServer _server = new();

    public HeldButtonWiringTests()
    {
        lock (Program.PhonesLock) { Program.Phones.Clear(); Program.Phones.Add(new Program.PairedPhone { Token = Token, Name = "t" }); Program.Phones.Add(new Program.PairedPhone { Token = TokenB, Name = "t2" }); }
        PowerWake.ForgetSwallowedButtonsForTests();
        PowerWake.NoteMonitorState(1);
        InputSender.ButtonSink = (b, d) => { lock (_l) _sent.Add((b, d)); };
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        InputSender.ButtonSink = null;
        PowerWake.NoteMonitorState(1);
        PowerWake.ForgetSwallowedButtonsForTests();
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
    }

    private List<(string, bool)> Sent() { lock (_l) return _sent.ToList(); }

    private static async Task Post(HttpClient c, string path, string json, string token = Token)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Program.HttpPort}{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Zephyr-VLC-Remote-Token", token);
        using var res = await c.SendAsync(req);
        Assert.Equal(200, (int)res.StatusCode);
    }

    [Fact]
    public async Task Http_ReleaseOfAHeldButton_ReachesTheMouse_WhenTheScreenWentDark()
    {
        using var c = new HttpClient();
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":true}");
        PowerWake.NoteMonitorState(0);
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":false}");
        Assert.Equal(new[] { ("left", true), ("left", false) }, Sent());
    }

    [Fact]
    public async Task Http_FirstPressOnADarkScreen_OnlyWakes_ThenTheSecondPressOnALitScreen_ReachesTheMouse()
    {
        PowerWake.NoteMonitorState(0);
        using var c = new HttpClient();
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":true}");
        Assert.Empty(Sent());                                      // dark: the press only wakes
        PowerWake.NoteMonitorState(1);                             // the wake lit the screen
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":true}");
        Assert.Equal(new[] { ("left", true) }, Sent());            // lit: the second press goes through to the sink
    }

    [Fact]
    public async Task Ws_ReleaseOfAHeldButton_ReachesTheMouse_WhenTheScreenWentDark()
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Zephyr-VLC-Remote-Token", Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Program.HttpPort}/ws"), CancellationToken.None);
        async Task Send(string j) => await ws.SendAsync(Encoding.UTF8.GetBytes(j), WebSocketMessageType.Text, true, CancellationToken.None);
        await Send("{\"cmd\":\"button\",\"button\":\"left\",\"down\":true}");
        await WaitFor(() => Sent().Count == 1);
        PowerWake.NoteMonitorState(0);
        await Send("{\"cmd\":\"button\",\"button\":\"left\",\"down\":false}");
        await WaitFor(() => Sent().Count == 2);
        Assert.Equal(new[] { ("left", true), ("left", false) }, Sent());
    }

    [Fact]
    public async Task Ws_ADroppedConnectionStillReleasesWhatItHeld()
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Zephyr-VLC-Remote-Token", Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Program.HttpPort}/ws"), CancellationToken.None);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"button\",\"button\":\"left\",\"down\":true}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitFor(() => Sent().Count == 1);
        ws.Abort();                                                   // no close frame, like a phone that died
        await WaitFor(() => Sent().Count == 2);
        Assert.Equal(("left", false), Sent()[1]);
    }

    private const string LeftDown = "{\"button\":\"left\",\"down\":true}", LeftUp = "{\"button\":\"left\",\"down\":false}";
    private const string RightDown = "{\"button\":\"right\",\"down\":true}", RightUp = "{\"button\":\"right\",\"down\":false}";

    [Fact]
    public async Task Http_WakeOnlyRightTap_ReachesNeitherAPressNorALoneRightUp()
    {
        PowerWake.NoteMonitorState(0);
        using var c = new HttpClient();
        await Post(c, "/mouse/button", RightDown);
        PowerWake.NoteMonitorState(1);                       // the wake lit the screen before the finger lifted
        await Post(c, "/mouse/button", RightUp);
        Assert.Empty(Sent());                                // no right-up reached the mouse: no context menu
        Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
        await Post(c, "/mouse/button", RightDown); await Post(c, "/mouse/button", RightUp);   // the next tap works normally
        Assert.Equal(new[] { ("right", true), ("right", false) }, Sent());
    }

    [Fact]
    public async Task Http_AnotherPhonesRelease_IsNotEatenByThisPhonesSwallowedPress()
    {
        PowerWake.NoteMonitorState(0);
        using var c = new HttpClient();
        await Post(c, "/mouse/button", LeftDown, Token);     // phone A: swallowed by the wake
        PowerWake.NoteMonitorState(1);
        await Post(c, "/mouse/button", LeftDown, TokenB);    // phone B: lit, delivered
        await Post(c, "/mouse/button", LeftUp, TokenB);      // B's release is delivered
        Assert.Equal(new[] { ("left", true), ("left", false) }, Sent());
        await Post(c, "/mouse/button", LeftUp, Token);       // A's own release is the one consumed
        Assert.Equal(2, Sent().Count);
    }

    [Fact]
    public async Task Ws_WakeOnlyRightTap_IsConsumedWhole_AndTheNextTapWorks()
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Zephyr-VLC-Remote-Token", Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Program.HttpPort}/ws"), CancellationToken.None);
        async Task Send(string j) => await ws.SendAsync(Encoding.UTF8.GetBytes(j), WebSocketMessageType.Text, true, CancellationToken.None);
        PowerWake.NoteMonitorState(0);
        await Send("{\"cmd\":\"button\",\"button\":\"right\",\"down\":true}");
        await WaitFor(() => PowerWake.SwallowedButtonCountForTests == 1);
        PowerWake.NoteMonitorState(1);
        await Send("{\"cmd\":\"button\",\"button\":\"right\",\"down\":false}");
        await WaitFor(() => PowerWake.SwallowedButtonCountForTests == 0);
        Assert.Empty(Sent());
        await Send("{\"cmd\":\"button\",\"button\":\"left\",\"down\":true}");
        await Send("{\"cmd\":\"button\",\"button\":\"left\",\"down\":false}");
        await WaitFor(() => Sent().Count == 2);
        Assert.Equal(new[] { ("left", true), ("left", false) }, Sent());
    }

    [Fact]
    public async Task ASwallowedPressOverTheLiveConnection_IsMatchedByItsReleaseOverHttp_SamePhoneOnly()
    {
        // The phone falls back to HTTP when its live connection is not open, so the owner is the phone, not the channel.
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("X-Zephyr-VLC-Remote-Token", Token);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Program.HttpPort}/ws"), CancellationToken.None);
        PowerWake.NoteMonitorState(0);
        await ws.SendAsync(Encoding.UTF8.GetBytes("{\"cmd\":\"button\",\"button\":\"left\",\"down\":true}"), WebSocketMessageType.Text, true, CancellationToken.None);
        await WaitFor(() => PowerWake.SwallowedButtonCountForTests == 1);
        ws.Abort();                                          // the live connection drops: nothing was injected, so nothing to release
        PowerWake.NoteMonitorState(1);
        using var c = new HttpClient();
        await Post(c, "/mouse/button", LeftUp, TokenB);      // another phone: delivered
        Assert.Equal(new[] { ("left", false) }, Sent());
        await Post(c, "/mouse/button", LeftUp, Token);       // the same phone: matches the swallowed press, consumed
        Assert.Single(Sent());
        Assert.Equal(0, PowerWake.SwallowedButtonCountForTests);
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
        Assert.True(done(), "timed out waiting for the helper");
    }
}
