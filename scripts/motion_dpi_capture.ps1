# Capture-only harness. App checkout/build stays frozen; never ships this script.
# DPI scaling uses undocumented Windows DisplayConfig packets. Measured window DPI is the gate.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing,System.Windows.Forms,UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;using System.Diagnostics;using System.Threading;
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
 public static string Scale(int percent){uint pc,mc;int rc=GetDisplayConfigBufferSizes(2,out pc,out mc);if(rc!=0)throw new Exception("config size "+rc);IntPtr p=Marshal.AllocHGlobal((int)pc*72),m=Marshal.AllocHGlobal((int)mc*64),q=Marshal.AllocHGlobal(32);try{rc=QueryDisplayConfig(2,ref pc,p,ref mc,m,IntPtr.Zero);if(rc!=0||pc==0)throw new Exception("config query "+rc);for(int i=0;i<32;i++)Marshal.WriteByte(q,i,0);Marshal.WriteInt32(q,0,-3);Marshal.WriteInt32(q,4,32);for(int i=0;i<12;i++)Marshal.WriteByte(q,8+i,Marshal.ReadByte(p,i));rc=DisplayConfigGetDeviceInfo(q);if(rc!=0)throw new Exception("DPI query "+rc);int min=Marshal.ReadInt32(q,20),cur=Marshal.ReadInt32(q,24),max=Marshal.ReadInt32(q,28);int target=(percent==150?2:percent==125?1:0)+min;if(target>max)throw new Exception("DPI target not supported");Marshal.WriteInt32(q,0,-4);Marshal.WriteInt32(q,4,24);Marshal.WriteInt32(q,20,target);rc=DisplayConfigSetDeviceInfo(q);if(rc!=0)throw new Exception("DPI set "+rc);return "requested="+percent+" minRel="+min+" priorRel="+cur+" targetRel="+target+" maxRel="+max;}finally{Marshal.FreeHGlobal(p);Marshal.FreeHGlobal(m);Marshal.FreeHGlobal(q);}}

}
'@
function SafeHash($path){try{return (Get-FileHash $path -Algorithm SHA256 -ErrorAction Stop).Hash}catch{return 'UNREADABLE'}}
function Shot($hw,$path){
 $r=[MotionProof+Rect]::new();if([MotionProof]::DwmGetWindowAttribute($hw,9,[ref]$r,16)-ne 0){throw 'DWM bounds missing'}
 $b=[System.Drawing.Bitmap]::new($r.R-$r.L,$r.B-$r.T);$g=[System.Drawing.Graphics]::FromImage($b)
 try{$g.CopyFromScreen($r.L,$r.T,0,0,$b.Size);if($script:burstClock){Write-Output "$path postCopyMs=$($script:burstClock.ElapsedMilliseconds)"};$b.Save($path,[System.Drawing.Imaging.ImageFormat]::Png)}finally{$g.Dispose();$b.Dispose()}
}
function Burst($hw,$x,$y,$prefix){
 [MotionProof]::SetCursorPos($x,$y)|Out-Null;Start-Sleep -Milliseconds 250;Shot $hw "$prefix-before.png"
 [MotionProof]::mouse_event(2,0,0,0,[UIntPtr]::Zero);[MotionProof]::mouse_event(4,0,0,0,[UIntPtr]::Zero)
 $sw=[Diagnostics.Stopwatch]::StartNew();$script:burstClock=$sw;$late=0;$first=-1
 foreach($ms in 40,130,220,310,400,600){while($sw.ElapsedMilliseconds-lt$ms){[Threading.Thread]::Sleep(1)};$actual=$sw.ElapsedMilliseconds;if($first-lt 0){$first=$actual};Shot $hw "$prefix-$($ms)ms.png";if($actual-$ms-gt 50){$late++};Write-Output "$prefix intendedMs=$ms captureStartMs=$actual lateOver50=$($actual-$ms-gt 50)"}
 Write-Output "$prefix firstCaptureStartOffsetMs=$first lateOver50FrameCount=$late";"$prefix firstCaptureStartOffsetMs=$first lateOver50FrameCount=$late"|Add-Content "$out/timing-summary.txt";$script:burstClock=$null
}
[MotionProof]::SetProcessDpiAwarenessContext([IntPtr]::new(-4))|Out-Null
$exe=(Resolve-Path windows/capture-out/ZephyrPlayer.exe).Path
$out=Join-Path $env:RUNNER_TEMP 'motion-dpi';New-Item -ItemType Directory -Force $out|Out-Null
$settings=Join-Path $env:APPDATA 'ZephyrPlayer/settings.json';New-Item -ItemType Directory -Force (Split-Path $settings)|Out-Null
$env:ZEPHYR_CI_CAPTURE='1'
# Same permission setup used by the passing full-desktop harness. Without it,
# a fresh runner is correctly held at its first-run network permission dialog.
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$setup=Start-Process $exe -ArgumentList '--install-firewall',$sid -PassThru -Wait
if($setup.ExitCode-ne 0){throw "Capture first-run setup failed: $($setup.ExitCode)"}
$shape=Start-Process $exe -ArgumentList '--firewall-shape-ok',('"'+$exe+'"') -PassThru -Wait
if($shape.ExitCode-ne 0){throw "Capture permission shape not ready: $($shape.ExitCode)"}
'CAPTURE_PERMISSION_SHAPE_PASS runner-only capture executable'|Set-Content "$out/startup-diagnostic.txt"
function FindId($pidValue,$id){
 $cond=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$pidValue)
 $roots=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)
 foreach($root in $roots){$child=$root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id));if($child){return $child}}
 return $null
}
try{
 foreach($scale in 100,125){
  try{[MotionProof]::Scale($scale)|Tee-Object -FilePath "$out/dpi.txt" -Append}catch{"DPI_UNAVAILABLE requested=$scale reason=$_"|Tee-Object -FilePath "$out/dpi.txt" -Append;throw};Start-Sleep -Seconds 3
  foreach($style in 'orange','white'){
   foreach($full in $false,$true){
    $script:burstClock=$null
    Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Stop-Process -Force;Start-Sleep -Milliseconds 500
    @{BarColorStyle=$style;ButtonFlash=$true;SecondarySkipSeconds=30;PrimarySkipSeconds=10}|ConvertTo-Json|Set-Content $settings
    $argsList=@('--play='+[Uri]::new((Resolve-Path 'motion-fixture.mp4').Path).AbsoluteUri);if($full){$argsList+='--fullscreen'}
    $p=Start-Process $exe -ArgumentList $argsList -PassThru
    $btn=$null;for($i=0;$i-lt 30;$i++){Start-Sleep -Milliseconds 300;$btn=FindId $p.Id 'SecondarySkipBackButton';if($btn){break}}
    if(!$btn){
     $roots=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$p.Id))
     foreach($root in $roots){"WINDOW_DIAGNOSTIC name=$($root.Current.Name) hwnd=$($root.Current.NativeWindowHandle)"|Add-Content "$out/startup-diagnostic.txt";if($root.Current.NativeWindowHandle-ne 0){Shot ([IntPtr]$root.Current.NativeWindowHandle) "$out/startup-$($root.Current.NativeWindowHandle).png"}}
     $crash=Join-Path $env:TEMP 'ZephyrPlayer-crash.log';if(Test-Path $crash){Copy-Item $crash "$out/startup-crash.log"}
     throw 'Secondary button missing'
    }
    $rootCond=[System.Windows.Automation.AndCondition]::new([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$p.Id),[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty,'Zephyr Player'))
    $root=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$rootCond)
    if(!$root){throw 'Actual player window missing'}
    $hw=[IntPtr]$root.Current.NativeWindowHandle
    if(!$full){[MotionProof]::SetWindowPos($hw,[IntPtr]::new(-1),0,0,1000,720,0)|Out-Null}else{[MotionProof]::SetWindowPos($hw,[IntPtr]::new(-1),0,0,0,0,0x43)|Out-Null}
    Start-Sleep -Seconds 1
    Shot $hw "$out/$scale-$style-$full-startup.png"
    $p.Refresh();"mainModule=$($p.MainModule.FileName) mainExeSHA256=$((SafeHash $p.MainModule.FileName))"|Add-Content "$out/runtime-binding.txt"
    Get-ChildItem (Split-Path $exe) | ForEach-Object {"publishedFile=$($_.Name) size=$($_.Length)"}|Add-Content "$out/publish-listing.txt"
    $modules=@($p.Modules)
    $modules|ForEach-Object {"module=$($_.ModuleName) SHA256=$((SafeHash $_.FileName)) fileVersion=$($_.FileVersionInfo.FileVersion) productVersion=$($_.FileVersionInfo.ProductVersion) path=$($_.FileName)"}|Add-Content "$out/runtime-binding.txt"
    # Expected hashes independently read from exact official package entries in
    # local preflight; not PE semantic-version fields or cache-location guesses.
    foreach($spec in @(@('coreclr.dll','Microsoft.NETCore.App.Runtime.win-x648.0.33','76808CF5616DB4FEC830E0884877BFEC0D2CC14F569CF1DCE8C90E747AE5D3AE'),@('libvlc.dll','VideoLAN.LibVLC.Windows3.0.24build/x64','C0B1BEDBC8E11267B39111733920A6AF73D08791E333E4F8902EAFFE4DAF6DB9'))){
     $loaded=@($modules|Where-Object ModuleName -eq $spec[0]);$expectedHash=$spec[2]
     if($loaded.Count-ne 1){"IDENTITY_UNVERIFIED module=$($spec[0]) loadedCount=$($loaded.Count) expectedPackage=$($spec[1]) expectedSHA256=$expectedHash"|Add-Content "$out/runtime-binding.txt";$script:identityFailed=$true;continue}
     $actualHash=SafeHash $loaded[0].FileName
     "module=$($spec[0]) loadedSHA256=$actualHash resolvedPackageSHA256=$expectedHash expectedPackage=$($spec[1]) equal=$($actualHash-eq$expectedHash)"|Add-Content "$out/runtime-binding.txt"
     if($actualHash-ne$expectedHash){$script:identityFailed=$true}
    }
    $dpi=[MotionProof]::GetDpiForWindow($hw);"scale=$scale style=$style fullscreen=$full dpi=$dpi"|Tee-Object -FilePath "$out/dpi.txt" -Append
    if($dpi-ne[uint32](96*$scale/100)){throw "Requested actualDPI unavailable: $dpi"}
    foreach($id in 'SecondarySkipBackButton','SecondarySkipForwardButton'){
     $btn=FindId $p.Id $id;if(!$btn){throw "$id missing"};$r=$btn.Current.BoundingRectangle
     if($r.Width-le 0-or$r.Height-le 0){throw "$id invisible"}
     Burst $hw ([int]($r.X+$r.Width/2)) ([int]($r.Y+$r.Height/2)) "$out/$scale-$style-$full-$id"
    }
    Stop-Process -Id $p.Id -Force
   }
  }
 }
 if($script:identityFailed){throw 'Actual runtime module identity did not match resolved package bytes; visual evidence retained'}
 }finally{
 "requestedDpiSamples=100,125;150UNVERIFIED runnermax125; identityFailed=$script:identityFailed"|Set-Content "$out/summary.txt"
 Get-ChildItem $out -Filter '*.png'|Group-Object {$_.BaseName -replace '-SecondarySkip.*',''}|ForEach-Object {"variant=$($_.Name) PNGcount=$($_.Count)"}|Add-Content "$out/summary.txt"
 if(Test-Path "$out/dpi.txt"){Get-Content "$out/dpi.txt"|Add-Content "$out/summary.txt"}
 if(Test-Path "$out/runtime-binding.txt"){Get-Content "$out/runtime-binding.txt"|Where-Object {$_-match 'loadedSHA256|IDENTITY_UNVERIFIED'}|Add-Content "$out/summary.txt"}
 Get-Process ZephyrPlayer -ErrorAction SilentlyContinue|Stop-Process -Force;try{[MotionProof]::Scale(100)|Out-Null}catch{Write-Warning $_}}
