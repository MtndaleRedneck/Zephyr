package com.bingemote
import androidx.test.platform.app.InstrumentationRegistry
import kotlinx.coroutines.*
import okhttp3.mockwebserver.*
import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.TimeUnit
class PlaybackRoutingTest {
 @Test fun noStatusDoesNotInventSeekOrVolumeAndBackgroundUsesSelectedPlayer() = runBlocking {
  val z=MockWebServer(); val v=MockWebServer();z.start();v.start()
  val ctx=InstrumentationRegistry.getInstrumentation().targetContext
  val s=Store(ctx);s.selectedSource=Source.ZEPHYR;s.zephyrHost="127.0.0.1";s.zephyrPort=z.port;s.zephyrToken="test-token"
  s.host="127.0.0.1";s.vlcPort=v.port;s.helperPort=v.port;s.vlcPassword="test"
  val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main)
  try {
   val r=RemoteState(ctx,s,scope);assertNull(r.status);r.skip(10);r.volumeStep(false)
   assertNull(z.takeRequest(1,TimeUnit.SECONDS));assertNull(v.takeRequest(1,TimeUnit.SECONDS))
   val controls=SelectedPlayback(s)
   z.enqueue(MockResponse().setBody("""{"state":"Playing","time":50,"length":500,"volume":60}"""))
   z.enqueue(MockResponse().setBody("{}"));controls.skip(30)
   assertEquals("/control/status",z.takeRequest().path)
   val seek=z.takeRequest();assertEquals("/control/seek",seek.path);assertTrue(seek.body.readUtf8().contains("80"))
   z.enqueue(MockResponse().setBody("""{"state":"Playing","time":80,"length":500}"""))
   z.enqueue(MockResponse().setBody("{}"));controls.togglePause()
   assertEquals("/control/status",z.takeRequest().path);assertEquals("/control/pause",z.takeRequest().path)
   s.selectedSource=Source.VLC
   try { controls.skip(10);fail("Source change must drop queued background command") } catch (_: IllegalStateException) {}
   assertNull("background selected Zephyr must never drive VLC",v.takeRequest(1,TimeUnit.SECONDS))
  } finally { scope.cancel();s.selectedSource=Source.VLC;s.zephyrToken="";z.shutdown();v.shutdown() }
 }
 @Test fun selectedZephyrGetsStatusAndCommandsWithoutVlcTraffic() = runBlocking {
  val z=MockWebServer(); val v=MockWebServer(); z.start(); v.start()
  val scope=CoroutineScope(SupervisorJob()+Dispatchers.Main)
  val ctx=InstrumentationRegistry.getInstrumentation().targetContext
  val s=Store(ctx); s.selectedSource=Source.ZEPHYR; s.zephyrHost="127.0.0.1";s.zephyrPort=z.port;s.zephyrToken="test-token"
  s.host="127.0.0.1";s.vlcPort=v.port;s.helperPort=v.port;s.setupDone=true;s.vlcPassword="test"
  val r=RemoteState(ctx,s,scope)
  try {
   z.enqueue(MockResponse().setBody("""{"state":"Playing","time":50,"length":500,"volume":60,"muted":false,"fullscreen":false}"""))
   r.pollOnce(); assertEquals("/control/status",z.takeRequest(2,TimeUnit.SECONDS)!!.path)
   assertEquals(50,r.status!!.timeSec);assertFalse(r.unreachable)
   z.enqueue(MockResponse().setBody("{}"));r.skip(10)
   val skip=z.takeRequest(3,TimeUnit.SECONDS)!!;assertEquals("/control/seek",skip.path);assertTrue(skip.body.readUtf8().contains("60"))
   z.enqueue(MockResponse().setBody("{}"));r.togglePause();assertEquals("/control/pause",z.takeRequest(3,TimeUnit.SECONDS)!!.path)
   z.enqueue(MockResponse().setBody("{}"));r.volumeStep(true);assertEquals("/control/volume",z.takeRequest(3,TimeUnit.SECONDS)!!.path)
   assertNull("selected Zephyr must never talk to VLC/helper",v.takeRequest(1,TimeUnit.SECONDS))
  } finally { scope.cancel();s.selectedSource=Source.VLC;s.zephyrToken="";z.shutdown();v.shutdown() }
 }
}
