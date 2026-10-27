using Xunit;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ZephyrVlcRemote.Tests;

/// <summary>
/// The settled three-app UDP coexistence on one PC: BingeMote keeps 8489, the Zephyr
/// Player keeps 8490, Zephyr VLC Remote owns 8491. Each answers the shared wire
/// question with its own identity and its own advertised HTTP port, whichever order
/// the apps start in. The phone reads the magic to know what it found, so a BingeMote
/// install is never mistaken for ours.
/// </summary>
public class CoexistenceTests
{
    private const string Question = "BINGEMOTE_DISCOVER_V1";

    private static UdpClient FakeResponder(int port, string replyMagic, int httpPort)
    {
        var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        sock.Bind(new IPEndPoint(IPAddress.Loopback, port));
        var udp = new UdpClient { Client = sock };
        var payload = Encoding.UTF8.GetBytes($"{replyMagic}|TESTPC|{httpPort}");
        Task.Run(async () =>
        {
            while (true)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(); }
                catch { return; }
                try { await udp.SendAsync(payload, r.RemoteEndPoint); } catch { }
            }
        });
        return udp;
    }

    private static string Ask(int port, int waitMs = 1500)
    {
        using var asker = new UdpClient();
        asker.Client.ReceiveTimeout = waitMs;
        asker.Send(Encoding.UTF8.GetBytes(Question), Question.Length, new IPEndPoint(IPAddress.Loopback, port));
        var any = new IPEndPoint(IPAddress.Any, 0);
        return Encoding.UTF8.GetString(asker.Receive(ref any)).Trim();
    }

    private static void AssertAllThreeAnswer()
    {
        Assert.Equal("BINGEMOTE_HERE_V1|TESTPC|8488", Ask(8489));
        Assert.Equal("ZEPHYR_PLAYER_HERE_V1|TESTPC|47823", Ask(8490));
        // The real responder under test: our magic and our HTTP port.
        Assert.Equal($"ZEPHYR_VLC_REMOTE_HERE_V1|{Program.MachineLabel()}|8492", Ask(8491));
    }

    [Fact]
    public void All_Three_Answer_With_Own_Identities_Ours_Started_First()
    {
        using var ours = new DiscoveryResponder();
        ours.Start();
        using var binge = FakeResponder(8489, "BINGEMOTE_HERE_V1", 8488);
        using var player = FakeResponder(8490, "ZEPHYR_PLAYER_HERE_V1", 47823);
        AssertAllThreeAnswer();
    }

    [Fact]
    public void All_Three_Answer_With_Own_Identities_Ours_Started_Last()
    {
        using var binge = FakeResponder(8489, "BINGEMOTE_HERE_V1", 8488);
        using var player = FakeResponder(8490, "ZEPHYR_PLAYER_HERE_V1", 47823);
        using var ours = new DiscoveryResponder();
        ours.Start();
        AssertAllThreeAnswer();
    }

    [Fact]
    public void Our_Port_Is_Claimed_Exclusively()
    {
        using var ours = new DiscoveryResponder();
        ours.Start();
        // A second exclusive bind on 8491 must fail: the port is ours alone.
        using var late = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { ExclusiveAddressUse = true };
        Assert.Throws<SocketException>(() => late.Bind(new IPEndPoint(IPAddress.Loopback, 8491)));
    }
}
