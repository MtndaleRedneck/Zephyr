using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
namespace ZephyrPlayer.Tests {
 public class RedesignRenderTests {
  [Theory]
  [InlineData(900,600,96)] [InlineData(900,600,120)] [InlineData(900,600,144)] [InlineData(900,600,192)]
  public void Settings_All_Sections_Render(int width,int height,int dpi) {
   Exception? error=null;var thread=new Thread(()=>{
    try {
     var dir=Path.Combine(Path.GetTempPath(),"zephyr-redesign-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
     var settings=new PlayerSettings(Path.Combine(dir,"settings.json"));
     var window=new SettingsWindow(settings,new PairedStore(Path.Combine(dir,"pairs.json")));
     // CP3 item 69: the compact window is 900x600, not resizable.
     Assert.Equal(900,window.Width);Assert.Equal(600,window.Height);Assert.Equal(ResizeMode.NoResize,window.ResizeMode);
     Assert.Equal("Zephyr Settings",window.Title);
     foreach(var section in new[]{"volume","skip","bar","remote","feedback","updates"}) {
      window.SelectSection(section);
      Assert.Equal(section,window.CurrentSection);
      var root=(FrameworkElement)window.Content;root.Measure(new Size(width,height));root.Arrange(new Rect(0,0,width,height));root.UpdateLayout();
      var image=new RenderTargetBitmap(width*dpi/96,height*dpi/96,dpi,dpi,PixelFormats.Pbgra32);image.Render(root);
      var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));
      using(var file=File.Create(Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")??Path.GetTempPath(),$"pc-redesign-{section}-{width}-{dpi}.png")))png.Save(file);
      Assert.True(root.ActualWidth>0);
     }
     window.Close();
    }catch(Exception ex){error=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
   });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(90000));Assert.Null(error);
  }

  /// <summary>CP3 items 70-72: the nav (four at the top, two pinned at the bottom), each
  /// section's title, the real version line, and no Movies section.</summary>
  [Fact]
  public void Settings_Nav_Titles_Version() {
   Exception? error=null;var thread=new Thread(()=>{
    try {
     var dir=Path.Combine(Path.GetTempPath(),"zephyr-nav-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
     var window=new SettingsWindow(new PlayerSettings(Path.Combine(dir,"settings.json")),new PairedStore(Path.Combine(dir,"pairs.json")));
     var titles=new System.Collections.Generic.Dictionary<string,string>{{"volume","Volume"},{"skip","Skip buttons"},{"bar","Playback bar"},{"remote","Paired phones"},{"feedback","Feedback & bugs"},{"updates","Updates"}};
     foreach(var kv in titles){window.SelectSection(kv.Key);Assert.Equal(kv.Value,((System.Windows.Controls.TextBlock)window.FindName("SectionTitle")!).Text);}
     Assert.Null(window.FindName("NavMovies"));Assert.Null(window.FindName("SectionMovies"));
     var v=typeof(SettingsWindow).Assembly.GetName().Version!;
     Assert.Equal("Zephyr Player "+v.ToString(3),((System.Windows.Controls.TextBlock)window.FindName("NavVersionText")!).Text);
     window.SelectSection("movies");Assert.Equal("volume",window.CurrentSection);
     // Boost is kept on the Volume tab (U1).
     window.SelectSection("volume");
     var boost=(System.Windows.Controls.WrapPanel)window.FindName("BoostPanel")!;
     Assert.Equal(PlayerSettings.BoostChoices.Length,boost.Children.Count);
     window.Close();
    }catch(Exception ex){error=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
   });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(60000));Assert.Null(error);
  }

  /// <summary>CP3 item 73: the LIVE PREVIEW is the real PlayerBar and follows each setting.</summary>
  [Fact]
  public void Settings_LivePreview_FollowsSettings() {
   Exception? error=null;var thread=new Thread(()=>{
    try {
     var dir=Path.Combine(Path.GetTempPath(),"zephyr-preview-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
     var settings=new PlayerSettings(Path.Combine(dir,"settings.json"));
     var window=new SettingsWindow(settings,new PairedStore(Path.Combine(dir,"pairs.json")));
     window.Show();
     void Pump()=>window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.Background);
     Assert.IsType<PlayerBar>(window.BarPreviewBar);Assert.True(window.BarPreviewBar.IsPreview);
     var (skip,bar)=window.PreviewState();
     Assert.Equal("10s",skip["primary_text"]);Assert.Equal("30s",skip["secondary_text"]);
     Assert.Equal("#FFF5F5F7",bar["primary_ring"]);Assert.Equal("#FFFF9500",bar["secondary_ring"]);
     settings.PrimarySkipSeconds=15;settings.SecondarySkipSeconds=60;Pump();
     (skip,bar)=window.PreviewState();
     Assert.Equal("15s",skip["primary_text"]);Assert.Equal("1m",skip["secondary_text"]);Assert.Equal("15s",bar["primary_text"]);
     settings.BarColorStyle="orange";Pump();
     (_,bar)=window.PreviewState();Assert.Equal("#FFFF9500",bar["primary_ring"]);
     settings.BarColorStyle="twotone";Pump();
     (_,bar)=window.PreviewState();Assert.Equal("#FFFF9500",bar["primary_ring"]);Assert.Equal("#FFF5F5F7",bar["primary_label"]);
     settings.IconStyle="filled";Pump();
     (_,bar)=window.PreviewState();Assert.Equal("#FFFF9500",bar["play_fill"]);
     settings.ShowSpeed=false;settings.ShowSleepTimer=false;Pump();
     (_,bar)=window.PreviewState();Assert.Equal(false,bar["speed_visible"]);Assert.Equal(false,bar["sleep_visible"]);
     // A chip click writes the setting (and so the preview).
     window.SelectSection("skip");window.UpdateLayout();
     var chips=(System.Windows.Controls.WrapPanel)window.FindName("PrimarySkipPanel")!;
     var five=(System.Windows.Controls.RadioButton)chips.Children[0];
     five.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));Pump();
     Assert.Equal(5,settings.PrimarySkipSeconds);
     (skip,_)=window.PreviewState();Assert.Equal("5s",skip["primary_text"]);
     window.Close();
    }catch(Exception ex){error=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
   });thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(60000));Assert.Null(error);
  }
 }
}
