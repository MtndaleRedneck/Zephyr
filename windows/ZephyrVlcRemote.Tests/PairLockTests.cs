using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Xunit;
namespace ZephyrVlcRemote.Tests;
[Collection("ProgramState")]
public class PairLockTests {
[Fact] public async Task FourStalledBodies_DoNotBlockCancel_AndValidPairSucceedsAfterDeadline() {
var dir=Path.Combine(Path.GetTempPath(),"pair-lock-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
Program.RedirectDataDirForTests(dir);VlcSetup.VlcDirOverride=dir;Program.StoreBroken=false;
lock(Program.PhonesLock)Program.Phones.Clear();
var probe=new TcpListener(IPAddress.Loopback,0);probe.Start();var port=((IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
var clients=new List<TcpClient>();
try {
using var server=new HttpServer();server.Start(port);
lock(HttpServer.PairLock){Program.PairCode="123456";Program.PairCodeExpires=DateTime.UtcNow.AddMinutes(1);Program.PairFailures=0;}
for(int i=0;i<4;i++){var c=new TcpClient();clients.Add(c);await c.ConnectAsync(IPAddress.Loopback,port);await c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("POST /pair HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Length: 100\r\n\r\n{"));}
await Task.Delay(200);
var cancel=Task.Run(()=>{lock(HttpServer.PairLock)Program.PairCode=null;});
Assert.Same(cancel,await Task.WhenAny(cancel,Task.Delay(1000)));await cancel;
await Task.Delay(10500);
lock(HttpServer.PairLock){Program.PairCode="654321";Program.PairCodeExpires=DateTime.UtcNow.AddMinutes(1);}
using var http=new HttpClient{BaseAddress=new Uri("http://127.0.0.1:"+port)};
var result=await http.PostAsync("/pair",new StringContent("{\"code\":\"654321\"}",Encoding.UTF8,"application/json"));Assert.Equal(HttpStatusCode.OK,result.StatusCode);
}finally{foreach(var c in clients)c.Dispose();Program.PairCode=null;Program.PairFailures=0;lock(Program.PhonesLock)Program.Phones.Clear();VlcSetup.VlcDirOverride=null;Program.ResetDataDirForTests();try{Directory.Delete(dir,true);}catch{}}
}
}
