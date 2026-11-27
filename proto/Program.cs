using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

// Feasibility prototype only, not product code.
static class P
{
    static string Describe(IPAddress local)
    {
        try
        {
            string adapterGuid = null;
            int ifIndex = -1;
            string ifName = null;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.Equals(local))
                    {
                        adapterGuid = ni.Id;
                        ifName = ni.Name;
                        try { ifIndex = ni.GetIPProperties().GetIPv4Properties().Index; } catch { }
                    }
                }
            }
            if (adapterGuid == null) return "local=" + local + " adapter=none";
            string net = "nlm=unmatched";
            var mgr = (INetworkListManager)new NetworkListManagerClass();
            var en = mgr.GetNetworks(1);
            while (true)
            {
                INetwork n;
                uint got;
                int hr = en.Next(1, out n, out got);
                if (hr != 0 || got == 0) break;
                var ce = n.GetNetworkConnections();
                while (true)
                {
                    INetworkConnection c;
                    uint g2;
                    int hr2 = ce.Next(1, out c, out g2);
                    if (hr2 != 0 || g2 == 0) break;
                    Guid aid;
                    c.GetAdapterId(out aid);
                    if (string.Equals(aid.ToString("B"), adapterGuid, StringComparison.OrdinalIgnoreCase))
                    {
                        Guid nid;
                        n.GetNetworkId(out nid);
                        net = "networkId=" + nid + " category=" + (int)n.GetCategory() + " (0=Public,1=Private,2=Domain) name=" + n.GetName();
                    }
                }
            }
            return "local=" + local + " ifName=" + ifName + " ifIndex=" + ifIndex + " adapter=" + adapterGuid + " " + net;
        }
        catch (Exception e)
        {
            return "local=" + local + " describe-failed: " + e.GetType().Name + ": " + e.Message;
        }
    }

    static int Main(string[] a)
    {
        string mode = a.Length > 0 ? a[0] : "";
        if (mode == "mkcert")
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest("CN=netproto", ec, HashAlgorithmName.SHA256);
            using var c0 = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
            var pfx = c0.Export(X509ContentType.Pfx, "x");
            var c1 = new X509Certificate2(pfx, "x", X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            var st = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            st.Open(OpenFlags.ReadWrite); st.Add(c1); st.Close();
            string key = "";
            try { key = ((ECDsaCng)c1.GetECDsaPrivateKey()).Key.UniqueName; } catch (Exception e) { key = "keyname-failed " + e.Message; }
            Console.WriteLine("THUMB " + c1.Thumbprint);
            Console.WriteLine("SPKI " + Convert.ToHexString(SHA256.HashData(c1.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant());
            Console.WriteLine("KEYNAME " + key);
            return 0;
        }
        if (mode == "nlm")
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork) Console.WriteLine(Describe(ua.Address));
                }
            }
            return 0;
        }
        if (mode == "serve")
        {
            var l = new HttpListener();
            l.Prefixes.Add(a[1] + "://+:" + a[2] + "/");
            try { l.Start(); }
            catch (Exception e) { Console.WriteLine("START-FAILED " + e.GetType().Name + ": " + e.Message); return 2; }
            Console.WriteLine("STARTED as " + Environment.UserName + " " + a[1] + " " + a[2]);
            Console.Out.Flush();
            var end = DateTime.UtcNow.AddSeconds(int.Parse(a[3]));
            while (DateTime.UtcNow < end)
            {
                var task = l.GetContextAsync();
                if (!task.Wait(1000)) continue;
                var ctx = task.Result;
                var local = ctx.Request.LocalEndPoint.Address;
                string d = Describe(local);
                Console.WriteLine("REQ from=" + ctx.Request.RemoteEndPoint.Address + " " + d);
                Console.Out.Flush();
                var bytes = Encoding.UTF8.GetBytes(d);
                ctx.Response.StatusCode = 200;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
            l.Stop();
            return 0;
        }
        if (mode == "client")
        {
            string pin = a[2].ToLowerInvariant();
            string seen = "";
            var h = new HttpClientHandler();
            h.ServerCertificateCustomValidationCallback = (m, cert, chain, err) =>
            {
                seen = Convert.ToHexString(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
                return seen == pin;
            };
            try
            {
                using var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(8) };
                var r = c.GetAsync(a[1]).GetAwaiter().GetResult();
                Console.WriteLine("CLIENT " + a[1] + " status=" + (int)r.StatusCode + " pinMatched=" + (seen == pin) + " body=" + r.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            }
            catch (Exception e)
            {
                Console.WriteLine("CLIENT " + a[1] + " failed pinMatched=" + (seen == pin) + " seenSpki=" + seen + " " + e.GetType().Name + ": " + (e.InnerException != null ? e.InnerException.Message : e.Message));
            }
            return 0;
        }
        if (mode == "udp")
        {
            var ip = IPAddress.Parse(a[1]);
            int port = int.Parse(a[2]);
            var rx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            rx.Bind(new IPEndPoint(IPAddress.Any, port));
            rx.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            var tx = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            tx.SendTo(Encoding.ASCII.GetBytes("hi"), new IPEndPoint(ip, port));
            var buf = new byte[64];
            EndPoint rep = new IPEndPoint(IPAddress.Any, 0);
            SocketFlags fl = SocketFlags.None;
            rx.ReceiveTimeout = 3000;
            try
            {
                int n = rx.ReceiveMessageFrom(buf, 0, buf.Length, ref fl, ref rep, out IPPacketInformation pi);
                Console.WriteLine("UDP sentTo=" + ip + " packetInfoAddress=" + pi.Address + " ifIndex=" + pi.Interface + " from=" + rep);
            }
            catch (Exception e) { Console.WriteLine("UDP failed " + e.GetType().Name + ": " + e.Message); }
            return 0;
        }
        if (mode == "coexist")
        {
            var l = new HttpListener();
            l.Prefixes.Add("http://+:" + a[1] + "/");
            l.Prefixes.Add("https://+:" + a[1] + "/");
            try { l.Start(); Console.WriteLine("COEXIST started both on " + a[1]); l.Stop(); }
            catch (Exception e) { Console.WriteLine("COEXIST failed " + e.GetType().Name + ": " + e.Message); }
            return 0;
        }
        Console.WriteLine("modes: nlm serve client udp coexist");
        return 1;
    }
}

[ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")] class NetworkListManagerClass { }
[ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface INetworkListManager
{
    IEnumNetworks GetNetworks(int flags);
    INetwork GetNetwork(Guid id);
    IEnumNetworkConnections GetNetworkConnections();
    INetworkConnection GetNetworkConnection(Guid id);
    bool IsConnectedToInternet { get; }
    bool IsConnected { get; }
    int GetConnectivity();
}
[ComImport, Guid("DCB00003-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IEnumNetworks
{
    [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
    [PreserveSig] int Next(uint celt, out INetwork n, out uint fetched);
    void Skip(uint celt);
    void Reset();
    IEnumNetworks Clone();
}
[ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface IEnumNetworkConnections
{
    [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
    [PreserveSig] int Next(uint celt, out INetworkConnection n, out uint fetched);
    void Skip(uint celt);
    void Reset();
    IEnumNetworkConnections Clone();
}
[ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface INetwork
{
    [return: MarshalAs(UnmanagedType.BStr)] string GetName();
    void SetName([MarshalAs(UnmanagedType.BStr)] string s);
    [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
    void SetDescription([MarshalAs(UnmanagedType.BStr)] string s);
    void GetNetworkId(out Guid id);
    int GetDomainType();
    IEnumNetworkConnections GetNetworkConnections();
    void GetTimeCreatedAndConnected(out uint a, out uint b, out uint c, out uint d, out uint e, out uint f, out uint g, out uint h);
    bool IsConnectedToInternet { get; }
    bool IsConnected { get; }
    int GetConnectivity();
    int GetCategory();
    void SetCategory(int c);
}
[ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
interface INetworkConnection
{
    INetwork GetNetwork();
    bool IsConnectedToInternet { get; }
    bool IsConnected { get; }
    int GetConnectivity();
    void GetConnectionId(out Guid id);
    void GetAdapterId(out Guid id);
    int GetDomainType();
}
