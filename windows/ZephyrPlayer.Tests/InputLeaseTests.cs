using System;
using System.Collections.Generic;
using Xunit;
using System.Net.Http;
using System.Threading.Tasks;
namespace ZephyrPlayer {
public class LeaseTests {
class Sink:IInputSink {public List<string> Events=new();public void Move(float x,float y){} public void Button(string b,bool d)=>Events.Add(b+":"+d);public void Scroll(float d){} public void Zoom(float d){} public void TypeText(string s){} public void NamedKey(string k){} }
[Fact] public async Task RealServer_LostPhoneAndRevokedTokenReleaseHeldButtons() {
var sink=new Sink();var valid=true;
var probe=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);probe.Start();var port=((System.Net.IPEndPoint)probe.LocalEndpoint).Port;probe.Stop();
using var server=new PlaybackServer((u,p,s)=>Task.CompletedTask,t=>valid&&t=="A",port,true,input:sink);server.Start();
using var c=new HttpClient {BaseAddress=new Uri("http://127.0.0.1:"+port)};c.DefaultRequestHeaders.Add("Authorization","Bearer A");
async Task Down(string b){var r=await c.PostAsync("/input/button",new StringContent("{\"button\":\""+b+"\",\"down\":true}"));r.EnsureSuccessStatusCode();}
await Down("left");await Task.Delay(9000);Assert.Contains("left:False",sink.Events);
await Down("right");valid=false;await Task.Delay(1100);Assert.Contains("right:False",sink.Events);
}
[Fact] public void ClosedLeaseRejectsLatePressAndHeartbeat() { var s=new Sink();var l=new InputLease(s);var n=DateTime.UtcNow;l.Button("A","left",true,n);l.Close();l.Button("A","left",true,n);l.Renew("A",n);l.Close();Assert.Equal(new[]{"left:True","left:False"},s.Events); }
[Theory][InlineData(true,null,true)][InlineData(false,null,false)][InlineData(true,false,false)][InlineData(false,true,true)]
public void MutePollPreservesStateOnFailure(bool current,bool? observed,bool expected) => Assert.Equal(expected,VolumeService.MuteAfterPoll(current,observed));
[Fact] public void LostConnection_ReleasesAtDeadline_AndCannotBeReleasedByOtherPhone(){var s=new Sink();var l=new InputLease(s);var n=DateTime.UtcNow;l.Button("A","left",true,n);l.Button("B","left",false,n);Assert.Single(s.Events);l.Expire(n.AddSeconds(7),_=>true);Assert.Single(s.Events);l.Expire(n.AddSeconds(9),_=>true);Assert.Equal("left:False",s.Events[1]);}
[Fact] public void OwnerHeartbeatKeepsDrag_RevocationAndShutdownRelease(){var s=new Sink();var l=new InputLease(s);var n=DateTime.UtcNow;l.Button("A","right",true,n);l.Renew("B",n.AddSeconds(7));l.Expire(n.AddSeconds(9),_=>true);Assert.Equal(2,s.Events.Count);l.Button("A","left",true,n);l.Renew("A",n.AddSeconds(7));l.Expire(n.AddSeconds(9),_=>true);Assert.Equal(3,s.Events.Count);l.Expire(n.AddSeconds(10),_=>false);Assert.Equal(4,s.Events.Count);l.Button("A","right",true,n);l.ReleaseAll();Assert.Equal("right:False",s.Events[^1]);}
}}
