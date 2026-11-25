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
    private readonly List<(string Button, bool Down)> _sent = new();
    private readonly object _l = new();
    private readonly HttpServer _server = new();

    public HeldButtonWiringTests()
    {
        lock (Program.PhonesLock) { Program.Phones.Clear(); Program.Phones.Add(new Program.PairedPhone { Token = Token, Name = "t" }); }
        PowerWake.NoteMonitorState(1);
        InputSender.ButtonSink = (b, d) => { lock (_l) _sent.Add((b, d)); };
        _server.Start();
    }

    public void Dispose()
    {
        _server.Dispose();
        InputSender.ButtonSink = null;
        PowerWake.NoteMonitorState(1);
        lock (Program.PhonesLock) { Program.Phones.Clear(); }
    }

    private List<(string, bool)> Sent() { lock (_l) return _sent.ToList(); }

    private static async Task Post(HttpClient c, string path, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Program.HttpPort}{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Zephyr-VLC-Remote-Token", Token);
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

    [Fact]
    public async Task Http_ThePressTheWakeSwallowed_ThenItsRelease_ReachesTheMouseAsALoneUp()
    {
        // The documented cost of the stateless rule: a tap on a dark screen swallows the press, and its release
        // is still delivered (a button-up with no press before it).
        PowerWake.NoteMonitorState(0);
        using var c = new HttpClient();
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":true}");
        await Post(c, "/mouse/button", "{\"button\":\"left\",\"down\":false}");
        Assert.Equal(new[] { ("left", false) }, Sent());
    }

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(50);
        Assert.True(done(), "timed out waiting for the helper");
    }
}
