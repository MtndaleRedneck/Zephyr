using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;
namespace ZephyrPlayer.Tests;
public sealed class RealAudioFactAttribute : FactAttribute { public RealAudioFactAttribute() { if(Environment.GetEnvironmentVariable("ZEPHYR_REAL_AUDIO_TEST")!="1") Skip="Requires explicitly authorized real Windows audio endpoint; not headless proof."; } }
public class CoreAudioTests {
[Fact] public void ComMetadata_HasCorrectFirstSlotsAndHresults() {
var flags=BindingFlags.NonPublic;
var t=typeof(VolumeService).GetNestedType("IMMDeviceEnumerator",flags)!;
Assert.True(t.IsImport); Assert.Equal(new[]{"EnumAudioEndpoints","GetDefaultAudioEndpoint"},t.GetMethods().OrderBy(m=>m.MetadataToken).Select(m=>m.Name));
var e=typeof(VolumeService).GetNestedType("IAudioEndpointVolume",flags)!;
foreach(var m in new[]{t,e,typeof(VolumeService).GetNestedType("IMMDevice",flags)!}.SelectMany(i=>i.GetMethods())) {Assert.Equal(typeof(int),m.ReturnType);Assert.True((m.GetMethodImplementationFlags() & MethodImplAttributes.PreserveSig)!=0);}
Assert.Equal(typeof(Guid).MakeByRefType(),e.GetMethod("SetMasterVolumeLevelScalar")!.GetParameters()[1].ParameterType);
}
[Fact] public void RealComActivation_ReadOnly_EnumAndDefaultSlots() {
if (!OperatingSystem.IsWindows()) return;
var result = VolumeService.ProbeComSlots();
Console.WriteLine($"CoreAudio Enum HRESULT=0x{result.enumerateHr:X8}, collection={result.collection}; default HRESULT=0x{result.defaultHr:X8}, device={result.device}");
Assert.Equal(0, result.enumerateHr); Assert.True(result.collection);
Assert.True(result.defaultHr==0 || result.defaultHr==unchecked((int)0x80070490), "Unexpected HRESULT: "+result.defaultHr.ToString("X8"));
if(result.defaultHr==0)Assert.True(result.device);
}
// Deliberately opt-in: changes the host's real audio settings and restores them.
[RealAudioFact] public void RealEndpoint_OptIn_ReadSetMuteRestore() {
using var v=new VolumeService();Assert.True(v.Available,"No real audio endpoint. This is not hardware proof.");
var old=v.GetMaster();var mute=v.GetMute();Assert.InRange(old,0,100);
try {v.SetMaster(40);Assert.InRange(v.GetMaster(),39,41);v.SetMaster(60);Assert.InRange(v.GetMaster(),59,61);v.SetMute(true);Assert.True(v.GetMute());v.SetMute(false);Assert.False(v.GetMute());}
finally {v.SetMaster(old);v.SetMute(mute);}
}
}
