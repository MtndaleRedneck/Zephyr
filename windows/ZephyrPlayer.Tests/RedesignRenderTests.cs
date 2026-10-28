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
  [InlineData(1180,900,96)] [InlineData(900,650,96)] [InlineData(1180,900,120)] [InlineData(1180,900,144)] [InlineData(1180,900,192)]
  public void Settings_All_Sections_Render(int width,int height,int dpi) {
   Exception? error=null;var thread=new Thread(()=>{
    try {
     var dir=Path.Combine(Path.GetTempPath(),"zephyr-redesign-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
     var settings=new PlayerSettings(Path.Combine(dir,"settings.json"));
     var window=new SettingsWindow(settings,new PairedStore(Path.Combine(dir,"pairs.json")),()=>{});
     foreach(var section in new[]{"volume","skip","bar","remote","feedback","updates"}) {
      window.SelectSection(section);
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
 }
}
