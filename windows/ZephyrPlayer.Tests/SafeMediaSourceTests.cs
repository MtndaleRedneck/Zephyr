using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;
using Xunit;
namespace ZephyrPlayer.Tests;
public sealed class SafeMediaSourceTests
{
    [Fact]
    public void SubtitleWhitespaceFlood_IsLinearAndBounded()
    {
        var watch=System.Diagnostics.Stopwatch.StartNew();
        foreach(var text in new[]{new string('\n',2*1024*1024),string.Concat(System.Linq.Enumerable.Repeat(" \n",1024*1024))})
            Assert.Throws<InvalidDataException>(()=>SafeMediaSource.SubtitleExtension(text));
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2),"subtitle validation did not stay bounded");
        Assert.Equal(".srt",SafeMediaSource.SubtitleExtension("1\n00:00:00,000 --> 00:00:01,000\nCredits: https://example.invalid\n"));
    }
    [Fact]
    public void CallbackExceptions_NeverCrossNativeBoundary()
    {
        using var input=new SafeCallbackInput(new ThrowingStream());
        Assert.False(input.Open(out _));Assert.False(input.Seek(0));
        var pointer=System.Runtime.InteropServices.Marshal.AllocHGlobal(4);
        try{Assert.Equal(-1,input.Read(pointer,4));}finally{System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);}
        input.Close();
    }
    private sealed class ThrowingStream:Stream
    {
        public override bool CanSeek=>true;public override bool CanRead=>true;public override bool CanWrite=>false;
        public override long Length=>throw new IOException();public override long Position{get=>throw new IOException();set=>throw new IOException();}
        public override int Read(byte[] b,int o,int n)=>throw new IOException();public override long Seek(long o,SeekOrigin origin)=>throw new IOException();
        public override void Flush(){}public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
    [Theory]
    [InlineData("#EXTM3U\nhttp://127.0.0.1:9/probe")]
    [InlineData("[playlist]\nFile1=smb://host/share")]
    [InlineData("<?xml version=\"1.0\"?><playlist/>")]
    [InlineData("<asx><entry href=\"file://host/share\"/></asx>")]
    public void PlaylistBytes_HaveNoMovieDemux(string text) => Assert.Null(SafeMediaSource.DetectDemux(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void ActualMp4Signature_SelectsStrictMp4()
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4"));
        Assert.Equal("mp4",SafeMediaSource.DetectDemux(bytes));
        Assert.True(SafeMediaSource.ExtensionMatches(".mp4","mp4"));
        Assert.False(SafeMediaSource.ExtensionMatches(".mkv","mp4"));
    }
    [Theory]
    [InlineData("smb://127.0.0.1/share")]
    [InlineData("file://127.0.0.1/c$/probe")]
    [InlineData("http://127.0.0.1:9/probe")]
    public async Task RangeReader_RefusesRedirectWithoutFollowing(string target)
    {
        using var fixture=new Fixture((ctx,_) => {ctx.Response.StatusCode=302;ctx.Response.RedirectLocation=target;});
        Assert.Throws<InvalidDataException>(() => new PeerRangeStream(new Uri(fixture.Url)));
        await Task.Delay(100);Assert.Equal(1,fixture.Requests);
    }
    [Fact]
    public void RangeReader_RejectsWrongStart()
    {
        using var fixture=new Fixture((ctx,_) => {ctx.Response.StatusCode=206;ctx.Response.Headers["Content-Range"]="bytes 2-9/10";ctx.Response.ContentLength64=8;ctx.Response.OutputStream.Write(new byte[8]);});
        Assert.Throws<InvalidDataException>(()=>new PeerRangeStream(new Uri(fixture.Url)));
    }
    [Fact]
    public void RangeIgnoredOnSeek_IsRejected()
    {
        using var fixture=new Fixture((ctx,_)=>{ctx.Response.StatusCode=200;ctx.Response.ContentLength64=100;ctx.Response.OutputStream.Write(new byte[100]);});
        using var source=new PeerRangeStream(new Uri(fixture.Url));
        Assert.Throws<InvalidDataException>(()=>source.Seek(10,SeekOrigin.Begin));
    }
    [Fact]
    public void LengthChangeOnSeek_IsRejected()
    {
        using var fixture=new Fixture((ctx,n)=>{
            var start=long.Parse(ctx.Request.Headers["Range"]!.Split('=')[1].Split('-')[0]);var total=n==1?100:101;
            ctx.Response.StatusCode=206;ctx.Response.Headers["Content-Range"]=$"bytes {start}-{total-1}/{total}";ctx.Response.ContentLength64=total-start;ctx.Response.OutputStream.Write(new byte[total-(int)start]);
        });
        using var source=new PeerRangeStream(new Uri(fixture.Url));
        Assert.Throws<InvalidDataException>(()=>source.Seek(10,SeekOrigin.Begin));
    }
    [Fact]
    public void TotalPreparationDeadline_StopsDribblingBody()
    {
        using var fixture=new Fixture((ctx,_)=>{ctx.Response.StatusCode=200;ctx.Response.ContentLength64=100;for(int i=0;i<100;i++){ctx.Response.OutputStream.WriteByte(1);ctx.Response.OutputStream.Flush();Thread.Sleep(50);}});
        using var deadline=new CancellationTokenSource(300);
        // The deadline may land while the request is still connecting on a loaded
        // runner (CI run 41010514847) or while the body dribbles: both must cancel.
        var watch=System.Diagnostics.Stopwatch.StartNew();var b=new byte[10];
        Assert.ThrowsAny<OperationCanceledException>((Action)(()=>{using var source=new PeerRangeStream(new Uri(fixture.Url),deadline.Token);while(true)source.Read(b,0,b.Length);}));
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(2));
    }
    [Fact]
    public void ConcurrentAbort_ReleasesStalledReadAndCallbackOwnership()
    {
        using var fixture=new Fixture((ctx,_)=>{ctx.Response.StatusCode=200;ctx.Response.ContentLength64=100;ctx.Response.OutputStream.Write(new byte[3]);ctx.Response.OutputStream.Flush();Thread.Sleep(2500);});
        using var source=new PeerRangeStream(new Uri(fixture.Url));using var input=new SafeCallbackInput(source);
        var pointer=System.Runtime.InteropServices.Marshal.AllocHGlobal(128);
        try {
            var task=Task.Run(()=>{for(int i=0;i<10;i++)if(input.Read(pointer,128)<0)return;});
            Thread.Sleep(150);input.Abort();
            Assert.True(task.Wait(3000),"abort did not unblock callback");
            Assert.False(input.Seek(0));Assert.Equal(-1,input.Read(pointer,128));
        } finally {System.Runtime.InteropServices.Marshal.FreeHGlobal(pointer);}
    }
    [Fact]
    public void CallbackNative_SocketDropStopsWithoutHostCrash()
    {
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4"));
        using var fixture=new Fixture((ctx,_)=>{ctx.Response.StatusCode=200;ctx.Response.ContentLength64=bytes.Length;ctx.Response.OutputStream.Write(bytes,0,Math.Min(4096,bytes.Length));ctx.Response.Abort();});
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
        try {
            using var source=new SafeMediaSource(vlc,fixture.Url);
            try {player.Play(source.Media);Thread.Sleep(500);}
            finally {source.Abort();player.Stop();}
        } catch(IOException) { } catch(System.Net.Http.HttpRequestException) { }
        Assert.NotEqual(VLCState.Playing,player.State);
    }
    [Fact]
    public void RangeReader_100SeeksPreserveBytes()
    {
        var data=new byte[16384];new Random(7).NextBytes(data);
        using var fixture=new Fixture((ctx,_) => {
            var start=long.Parse(ctx.Request.Headers["Range"]!.Split('=')[1].Split('-')[0]);
            ctx.Response.StatusCode=206;ctx.Response.Headers["Content-Range"]=$"bytes {start}-{data.Length-1}/{data.Length}";ctx.Response.ContentLength64=data.Length-start;
            ctx.Response.OutputStream.Write(data,(int)start,data.Length-(int)start);
        });
        using var source=new PeerRangeStream(new Uri(fixture.Url));var random=new Random(9);var buffer=new byte[17];
        for(int i=0;i<100;i++){var offset=random.Next(0,data.Length-17);source.Seek(offset,SeekOrigin.Begin);Assert.Equal(17,source.Read(buffer,0,17));Assert.Equal(data.AsSpan(offset,17).ToArray(),buffer);}
    }
    [Theory]
    [InlineData("mkv")]
    [InlineData("mp4")]
    [InlineData("avformat")]
    public void ExplicitDemuxOnCallbacks_DoesNotFetchPlaylist(string demux)
    {
        using var target=new Fixture((ctx,_)=>{ctx.Response.StatusCode=404;});
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
        using var bytes=new MemoryStream(Encoding.UTF8.GetBytes("#EXTM3U\n"+target.Url+"\n"));
        using var input=new StreamMediaInput(bytes);
        using var media=new Media(vlc,input,":demux="+demux,":no-sub-autodetect-file");
        player.Play(media);Thread.Sleep(1200);player.Stop();
        Assert.Equal(0,target.Requests);
    }

    [Fact]
    public void CallbackMovie_StartsAndSeeks_WithStrictDemux()
    {
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
        vlc.Log+=(_,e)=>Console.WriteLine("CALLBACK_NATIVE "+e.Level+" "+e.Message);
        var path=Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4");
        using var source=new SafeMediaSource(vlc,new Uri(path).AbsoluteUri);
        Assert.True(player.Play(source.Media));Assert.True(SpinWait.SpinUntil(()=>player.State==VLCState.Playing,8000),"callback mp4 did not play");
        Assert.True(SpinWait.SpinUntil(()=>player.Time>300,4000),"callback MP4 did not advance decoded time");
        player.Time=1000;player.Stop();
    }
    [Fact]
    public void M3uNamedMkv_IsRejectedBeforeNativeAccess()
    {
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".mkv");File.WriteAllText(path,"#EXTM3U\nhttp://127.0.0.1:9/probe");
        try {Assert.Throws<InvalidDataException>(()=>new SafeMediaSource(vlc,new Uri(path).AbsoluteUri));}finally{File.Delete(path);}
    }
    [Fact]
    public void M3uNamedSrt_IsRejectedBeforeAddSlave()
    {
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");
        using var source=new SafeMediaSource(vlc,new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4")).AbsoluteUri);
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".srt");File.WriteAllText(path,"#EXTM3U\nhttp://127.0.0.1:9/probe");
        try {Assert.Throws<InvalidDataException>(()=>source.AddSubtitle(new Uri(path).AbsoluteUri));}finally{File.Delete(path);}
    }
    [Theory]
    [InlineData("<rss><channel><item><enclosure url=\"{0}\"/></item></channel></rss>")]
    [InlineData("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><link href=\"{0}\"/></entry></feed>")]
    [InlineData(" \n \t<?xml version=\"1.0\"?><playlist><location>{0}</location></playlist>")]
    [InlineData("\uFEFF<?xml version=\"1.0\"?><rss><channel><link>{0}</link></channel></rss>")]
    [InlineData("\n\n[playlist]\nFile1={0}")]
    public void NativeAddSlave_RefusesProbingPrefixesWithoutNetwork(string prefix)
    {
        using var target=new Fixture((ctx,_)=>{ctx.Response.StatusCode=404;});
        var text=prefix.Replace("{0}",target.Url)+"\n1\n00:00:00,000 --> 00:00:03,000\nCredits\n";
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".srt");File.WriteAllText(path,text);
        try{
            Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
            using var safe=new SafeMediaSource(vlc,new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4")).AbsoluteUri);
            Assert.Throws<InvalidDataException>(()=>safe.AddSubtitle(new Uri(path).AbsoluteUri));
            try{Assert.True(player.Play(safe.Media));Thread.Sleep(300);}finally{safe.Abort();player.Stop();}
            Assert.Equal(0,target.Requests);
        }finally{File.Delete(path);}
    }
    [Theory]
    [InlineData("x<rss><channel><item><enclosure url=\"{0}\"/></item></channel></rss>\n1\n00:00:00,000 --> 00:00:03,000\nCredits")]
    [InlineData("WEBVTT\n\n00:00:00.000 --> 00:00:03.000\nCaption [playlist] File1={0}")]
    [InlineData("1\n00:00:00,000 --> 00:00:03,000\n<asx><entry href=\"{0}\"/></asx>")]
    public void NativeAddSlave_AcceptedTextCannotFetchEmbeddedPlaylist(string content)
    {
        using var target=new Fixture((ctx,_)=>{ctx.Response.StatusCode=404;});
        using(var client=new System.Net.Http.HttpClient()){using var control=client.GetAsync(target.Url).GetAwaiter().GetResult();Assert.Equal(1,target.Requests);}
        var baseline=target.Requests;var text=content.Replace("{0}",target.Url);
        if(text.StartsWith("1\n"))text=text.Replace("<asx>","Dialogue: <asx>");
        var extension=SafeMediaSource.SubtitleExtension(text);Assert.True(extension is ".srt" or ".vtt");
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+extension);File.WriteAllText(path,text);
        try{
            Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
            using var safe=new SafeMediaSource(vlc,new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4")).AbsoluteUri);
            safe.AddSubtitle(new Uri(path).AbsoluteUri);
            try{Assert.True(player.Play(safe.Media));Assert.True(SpinWait.SpinUntil(()=>player.State==VLCState.Playing,8000));Thread.Sleep(2200);}
            finally{safe.Abort();player.Stop();player.Media=null;}
            Assert.Equal(baseline,target.Requests);
        }finally{File.Delete(path);}
    }
    [Fact]
    public void OrdinaryMkv_CallbackPlaybackWorksWithExternalFeaturesDisabled()
    {
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
        using var safe=new SafeMediaSource(vlc,new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mkv")).AbsoluteUri);
        try{Assert.True(player.Play(safe.Media));Assert.True(SpinWait.SpinUntil(()=>player.State==VLCState.Playing,8000));Assert.True(SpinWait.SpinUntil(()=>player.Time>300,4000),"callback MKV did not advance decoded time");}
        finally{safe.Abort();player.Stop();}
    }
    [Fact]
    public void Mp4ExternalDataReference_DoesNotRequestNetworkTarget()
    {
        using var target=new Fixture((ctx,_)=>{ctx.Response.StatusCode=404;});
        var bytes=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4"));
        var mutated=ExternalMp4Reference(bytes,target.Url);
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".mp4");File.WriteAllBytes(path,mutated);
        try{
            Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
            using var safe=new SafeMediaSource(vlc,new Uri(path).AbsoluteUri);
            try{player.Play(safe.Media);Thread.Sleep(1000);}finally{safe.Abort();player.Stop();}
            Assert.Equal(0,target.Requests);
        }finally{File.Delete(path);}
    }
    private static byte[] ExternalMp4Reference(byte[] source,string target)
    {
        var url=System.Text.Encoding.UTF8.GetBytes(target+"\0");
        var ancestors=new System.Collections.Generic.List<int>();var found=-1;
        bool Find(int start,int end){
            for(var at=start;at+8<=end;){
                var size=(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(at,4));
                if(size<8||at+size>end)break;
                var kind=System.Text.Encoding.ASCII.GetString(source,at+4,4);
                if(kind=="url "){found=at;return true;}
                if(new[]{"moov","trak","mdia","minf","dinf","dref"}.Contains(kind)){
                    ancestors.Add(at);if(Find(at+8+(kind=="dref"?8:0),at+size))return true;ancestors.RemoveAt(ancestors.Count-1);
                }
                at+=size;
            }return false;
        }
        Assert.True(Find(0,source.Length),"fixture had no data reference");
        var old=(int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(source.AsSpan(found,4));var replacement=new byte[12+url.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(replacement.AsSpan(0,4),(uint)replacement.Length);System.Text.Encoding.ASCII.GetBytes("url ").CopyTo(replacement,4);url.CopyTo(replacement,12);
        var output=new byte[source.Length+replacement.Length-old];source.AsSpan(0,found).CopyTo(output);replacement.CopyTo(output,found);source.AsSpan(found+old).CopyTo(output.AsSpan(found+replacement.Length));
        foreach(var at in ancestors){var n=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(output.AsSpan(at,4));System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(at,4),(uint)(n+replacement.Length-old));}
        return output;
    }
    [Fact]
    public void MkvOrderedExternalChapter_CallbackDefenseInDepth_NoPreloadLog()
    {
        var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            var fixtureRoot=Path.Combine(AppContext.BaseDirectory,"fixtures");var main=Path.Combine(root,"ordered.mkv");var sibling=Path.Combine(root,"sibling.mkv");
            File.Copy(Path.Combine(fixtureRoot,"ordered-external.mkv"),main);File.Copy(Path.Combine(fixtureRoot,"clip.mkv"),sibling);
            var chapterBytes=File.ReadAllBytes(main);Assert.True(chapterBytes.AsSpan().IndexOf(new byte[]{0x45,0xdd,0x81,1})>=0);
            var siblingBytes=File.ReadAllBytes(sibling);var uidAt=siblingBytes.AsSpan().IndexOf(new byte[]{0x73,0xa4,0x90});Assert.True(uidAt>=0);
            Assert.True(chapterBytes.AsSpan().IndexOf(siblingBytes.AsSpan(uidAt+3,16))>=0,"external chapter UID must match sibling");
            using var lockedSibling=new FileStream(sibling,FileMode.Open,FileAccess.Read,FileShare.None);
            Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
            // Callback access already lacks a local filename; this is defense-in-depth,
            // not proof that the two mkv options were necessary or sufficient.
            var preload=0;vlc.Log+=(_,args)=>{if(args.Message.Contains("Preloading local dir",StringComparison.Ordinal))Interlocked.Increment(ref preload);};
            using var safe=new SafeMediaSource(vlc,new Uri(main).AbsoluteUri);
            try{Assert.True(player.Play(safe.Media));Thread.Sleep(1000);Assert.Equal(0,preload);}
            finally{safe.Abort();player.Stop();player.Media=null;}
        }finally{Directory.Delete(root,true);}
    }
    [Fact]
    public void NativeParser_CancelThenDispose_DrainsCallbacks()
    {
        Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");
        var path=new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4")).AbsoluteUri;
        for(var i=0;i<20;i++) {
            using var safe=new SafeMediaSource(vlc,path);using var cancel=new CancellationTokenSource();
            var parsing=safe.Media.Parse(MediaParseOptions.ParseLocal,200,cancel.Token);
            cancel.Cancel();safe.Media.ParseStop();
            try{Assert.True(parsing.Wait(3000));}catch(AggregateException ex){Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);}
        }
    }
    [Fact]
    public void UiPreparation_HomeAndNewPlay_InvalidateOldCompletion()
    {
        Exception? failure=null;
        var thread=new Thread(()=>{
            try{
                Core.Initialize();using var vlc=new LibVLC("--no-video","--aout=dummy","--verbose=2");using var player=new LibVLCSharp.Shared.MediaPlayer(vlc);
                var settingsPath=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");
                var window=new PlayerView(vlc,player,new PlayerSettings(settingsPath),new VolumeService());
                // The player is a view in 2.0: host it in a plain window like the shell does.
                var host=new System.Windows.Window{Content=window,Width=640,Height=360,ShowInTaskbar=false};
                host.Show();
                try{
                    using var slow=new Fixture((ctx,_)=>{Thread.Sleep(800);ctx.Response.StatusCode=200;ctx.Response.ContentLength64=100;ctx.Response.OutputStream.Write(new byte[100]);});
                    var old=window.PlayFromUrlAsync(slow.Url,null,Array.Empty<string>());
                    var ping=false;window.Dispatcher.BeginInvoke(new Action(()=>ping=true));
                    PumpUntil(()=>ping,"preparation blocked UI");
                    window.StopPlaybackSource();PumpUntil(()=>old.IsCompleted,"Home did not cancel preparation");
                    ObserveCancellation(old);Assert.NotEqual(VLCState.Playing,player.State);
                    var previous=window.PlayFromUrlAsync(slow.Url,null,Array.Empty<string>());
                    var next=window.PlayFromUrlAsync(new Uri(Path.Combine(AppContext.BaseDirectory,"fixtures","clip.mp4")).AbsoluteUri,null,Array.Empty<string>());
                    PumpUntil(()=>next.IsCompleted,"new play did not complete");next.GetAwaiter().GetResult();
                    PumpUntil(()=>previous.IsCompleted,"superseded preparation did not stop");ObserveCancellation(previous);
                    PumpUntil(()=>player.State==VLCState.Playing,"stale preparation replaced newer movie");
                }finally{window.StopPlaybackSource();host.Close();try{File.Delete(settingsPath);}catch{}}
            }catch(Exception ex){failure=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(30000));if(failure!=null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void ObserveCancellation(Task task)
    {
        try{task.GetAwaiter().GetResult();}catch(OperationCanceledException){}catch(ObjectDisposedException){}
    }
    private static void PumpUntil(Func<bool> test,string message)
    {
        var until=Environment.TickCount64+8000;
        while(!test()&&Environment.TickCount64<until){var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);Thread.Sleep(10);}
        Assert.True(test(),message);
    }
    private sealed class Fixture:IDisposable
    {
        private readonly HttpListener _listener=new();private int _requests;
        public int Requests=>_requests;public string Url{get;}
        internal Fixture(Action<HttpListenerContext,int> reply)
        {
            var portProbe=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);portProbe.Start();var port=((IPEndPoint)portProbe.LocalEndpoint).Port;portProbe.Stop();
            Url=$"http://127.0.0.1:{port}/s/tok/1";_listener.Prefixes.Add($"http://127.0.0.1:{port}/");_listener.Start();
            _=Task.Run(async()=>{while(_listener.IsListening){HttpListenerContext ctx;try{ctx=await _listener.GetContextAsync();}catch{break;}try{reply(ctx,Interlocked.Increment(ref _requests));}catch{}finally{try{ctx.Response.Close();}catch{}}}});
        }
        public void Dispose()=>_listener.Close();
    }
}
