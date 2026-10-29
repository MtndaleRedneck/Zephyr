# Capture-only harness. App checkout/build stays frozen; never ships this script.
# DPI scaling uses undocumented Windows DisplayConfig packets. Measured window DPI is the gate.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms,UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Drawing;using System.Drawing.Imaging;using System.Diagnostics;using System.Threading;
public static class MotionProof {
 [StructLayout(LayoutKind.Sequential)]public struct Rect{public int L,T,R,B;}
 [DllImport("user32.dll")]public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
 [DllImport("user32.dll")]public static extern uint GetDpiForWindow(IntPtr h);
 [DllImport("user32.dll")]public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int ht,uint f);
 [DllImport("user32.dll")]public static extern bool SetCursorPos(int x,int y);
 [DllImport("user32.dll")]public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr ex);
 [DllImport("dwmapi.dll")]public static extern int DwmGetWindowAttribute(IntPtr h,int a,out Rect r,int n);
 [DllImport("user32.dll")]static extern int GetDisplayConfigBufferSizes(uint f,out uint p,out uint m);
 [DllImport("user32.dll")]static extern int QueryDisplayConfig(uint f,ref uint p,IntPtr paths,ref uint m,IntPtr modes,IntPtr top);
 [DllImport("user32.dll")]static extern int DisplayConfigGetDeviceInfo(IntPtr p);
 [DllImport("user32.dll")]static extern int DisplayConfigSetDeviceInfo(IntPtr p);
 public static string Scale(int percent){uint pc,mc;int rc=GetDisplayConfigBufferSizes(2,out pc,out mc);if(rc!=0)throw new Exception("config size "+rc);IntPtr p=Marshal.AllocHGlobal((int)pc*72),m=Marshal.AllocHGlobal((int)mc*64),q=Marshal.AllocHGlobal(32);try{rc=QueryDisplayConfig(2,ref pc,p,ref mc,m,IntPtr.Zero);if(rc!=0||pc==0)throw new Exception("config query "+rc);for(int i=0;i<32;i++)Marshal.WriteByte(q,i,0);Marshal.WriteInt32(q,0,-3);Marshal.WriteInt32(q,4,32);for(int i=0;i<12;i++)Marshal.WriteByte(q,8+i,Marshal.ReadByte(p,i));rc=DisplayConfigGetDeviceInfo(q);if(rc!=0)throw new Exception("DPI query "+rc);int min=Marshal.ReadInt32(q,20),cur=Marshal.ReadInt32(q,24),max=Marshal.ReadInt32(q,28);int target=(percent==150?2:0)+min;if(target>max)throw new Exception("DPI target not supported");Marshal.WriteInt32(q,0,-4);Marshal.WriteInt32(q,4,24);Marshal.WriteInt32(q,20,target);rc=DisplayConfigSetDeviceInfo(q);if(rc!=0)throw new Exception("DPI set "+rc);return "requested="+percent+" minRel="+min+" priorRel="+cur+" targetRel="+target+" maxRel="+max;}finally{Marshal.FreeHGlobal(p);Marshal.FreeHGlobal(m);Marshal.FreeHGlobal(q);}}
 public static void Shot(IntPtr h,string path){Rect r;if(DwmGetWindowAttribute(h,9,out r,16)!=0)throw new Exception("DWM bounds");using(var b=new Bitmap(r.R-r.L,r.B-r.T))using(var g=Graphics.FromImage(b)){g.CopyFromScreen(r.L,r.T,0,0,b.Size);b.Save(path,ImageFormat.Png);}}
 public static void Burst(IntPtr h,int x,int y,string prefix){SetCursorPos(x,y);Thread.Sleep(250);Shot(h,prefix+"-before.png");mouse_event(2,0,0,0,UIntPtr.Zero);mouse_event(4,0,0,0,UIntPtr.Zero);var sw=Stopwatch.StartNew();int late=0;long first=-1;foreach(var ms in new[]{40,130,220,310,400,600}){while(sw.ElapsedMilliseconds<ms)Thread.Sleep(1);long actual=sw.ElapsedMilliseconds;if(first<0)first=actual;Shot(h,prefix+"-"+ms+"ms.png");if(actual-ms>50)late++;Console.WriteLine(prefix+" intendedMs="+ms+" actualMs="+actual+" lateOver50="+(actual-ms>50));}Console.WriteLine(prefix+" firstCaptureOffsetMs="+first+" lateOver50FrameCount="+late);}
}
'@
[MotionProof]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))|Out-Null
$exe=(Resolve-Path windows/capture-out/ZephyrPlayer.exe).Path
$out=Join-Path $env:RUNNER_TEMP 'motion-dpi';New-Item -ItemType Directory -Force $out|Out-Null
$settings=Join-Path $env:APPDATA 'ZephyrPlayer/settings.json';New-Item -ItemType Directory -Force (Split-Path $settings)|Out-Null
$env:ZEPHYR_CI_CAPTURE='1'
function FindId($pidValue,$id){
 $cond=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$pidValue)
 $roots=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)
 foreach($root in $roots){$child=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id));if($child){return $child}}
 return $null
}
try{
 foreach($scale in 100,150){
  [MotionProof]::Scale($scale)|Tee-Object -FilePath "$out/dpi.txt" -Append;Start-Sleep -Seconds 3
  foreach($style in 'orange','white'){
   foreach($full in $false,$true){
    Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Stop-Process -Force;Start-Sleep -Milliseconds 500
    @{BarColorStyle=$style;ButtonFlash=$true;SecondarySkipSeconds=30;PrimarySkipSeconds=10}|ConvertTo-Json|Set-Content $settings
    $argsList=@('--play='+[Uri]::new((Resolve-Path 'motion-fixture.mp4').Path).AbsoluteUri);if($full){$argsList+='--fullscreen'}
    $p=Start-Process $exe -ArgumentList $argsList -PassThru
    $btn=$null;for($i=0;$i-lt 30;$i++){Start-Sleep -Milliseconds 300;$btn=FindId $p.Id 'SecondarySkipBackButton';if($btn){break}}
    if(!$btn){throw 'Secondary button missing'}
    $rootCond=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$p.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Zephyr Player'))
    $root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$rootCond)
    if(!$root){throw 'Actual player window missing'}
    $hw=[IntPtr]$root.Current.NativeWindowHandle
    if(!$full){[MotionProof]::SetWindowPos($hw,[IntPtr]::new(-1),0,0,1000,720,0)|Out-Null}else{[MotionProof]::SetWindowPos($hw,[IntPtr]::new(-1),0,0,0,0,0x43)|Out-Null}
    Start-Sleep -Seconds 1
    $p.Refresh();$modules=@($p.Modules);$core=@($modules|Where-Object ModuleName -eq coreclr.dll);$vlc=@($modules|Where-Object ModuleName -eq libvlc.dll);if($core.Count-ne 1-or!$core[0].FileVersionInfo.ProductVersion.StartsWith('8.0.33')){throw 'Actual coreclr8.0.33 binding not proven'};if($vlc.Count-ne 1-or!$vlc[0].FileVersionInfo.ProductVersion.StartsWith('3.0.24')){throw 'Actual nativeVLC3.0.24 binding not proven'};$modules|Where-Object {$_.ModuleName -in 'coreclr.dll','libvlc.dll','libvlccore.dll'}|ForEach-Object {"runtime module=$($_.ModuleName) fileVersion=$($_.FileVersionInfo.FileVersion) productVersion=$($_.FileVersionInfo.ProductVersion) path=$($_.FileName)"|Tee-Object -FilePath "$out/runtime-binding.txt" -Append}
    $dpi=[MotionProof]::GetDpiForWindow($hw);"scale=$scale style=$style fullscreen=$full dpi=$dpi"|Tee-Object -FilePath "$out/dpi.txt" -Append
    if($dpi-ne[uint32](96*$scale/100)){throw "Requested actualDPI unavailable: $dpi"}
    foreach($id in 'SecondarySkipBackButton','SecondarySkipForwardButton'){
     $btn=FindId $p.Id $id;if(!$btn){throw "$id missing"};$r=$btn.Current.BoundingRectangle
     if($r.Width-le 0-or$r.Height-le 0){throw "$id invisible"}
     [MotionProof]::Burst($hw,[int]($r.X+$r.Width/2),[int]($r.Y+$r.Height/2),"$out/$scale-$style-$full-$id")
    }
    Stop-Process -Id $p.Id -Force
   }
  }
 }
}finally{Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Stop-Process -Force;try{[MotionProof]::Scale(100)|Out-Null}catch{Write-Warning $_}}
