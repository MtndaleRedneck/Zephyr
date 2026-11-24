using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;
namespace ZephyrPlayer.Tests {
 /// <summary>Every themed control must draw from our own template, never the system look.</summary>
 public class ThemeChromeTests {
  static void Sta(Action body) {
   Exception? error=null;var t=new Thread(()=>{try{body();}catch(Exception ex){error=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}});
   t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(60000));Assert.Null(error);
  }
  static ResourceDictionary Theme()=>new ResourceDictionary{Source=new Uri("pack://application:,,,/ZephyrPlayer;component/Theme.xaml")};
  static void Walk(DependencyObject o,List<string> names){
   names.Add(o.GetType().FullName??"");
   for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)Walk(VisualTreeHelper.GetChild(o,i),names);
  }
  static List<string> Visuals(FrameworkElement e){
   // Popups (tooltip, context menu) cannot have a parent: theme and lay them out on their own.
   if(e is ToolTip||e is ContextMenu){e.Resources.MergedDictionaries.Add(Theme());e.ApplyTemplate();e.Measure(new Size(240,80));e.Arrange(new Rect(0,0,240,80));e.UpdateLayout();}
   else {var host=new Border{Background=Brushes.Black,Width=240,Height=80,Child=e};host.Resources.MergedDictionaries.Add(Theme());
    host.Measure(new Size(240,80));host.Arrange(new Rect(0,0,240,80));host.UpdateLayout();}
   var names=new List<string>();Walk(e,names);return names;
  }
  [Fact]
  public void Controls_Use_Our_Templates_Not_System_Chrome(){
   Sta(()=>{
    var cases=new Dictionary<string,FrameworkElement>{
     ["Button"]=new Button{Content="Browse"},["TextBox"]=new TextBox{Text="abc"},["RadioButton"]=new RadioButton{Content="10s"},
     ["CheckBox"]=new CheckBox{Content="x"},["ListBox"]=new ListBox{Items={new ListBoxItem{Content="a"}}},["ScrollBar"]=new ScrollBar(),
     ["ToolTip"]=new ToolTip{Content="tip"},["Menu"]=new ContextMenu{Items={new MenuItem{Header="Open"}}}};
    foreach(var (name,control) in cases){
     var visuals=Visuals(control);
     Assert.DoesNotContain(visuals,v=>v.StartsWith("Microsoft.Windows.Themes.")||v.Contains("SystemDropShadowChrome")||v.Contains("ClassicBorderDecorator"));
     if(control is Control c)Assert.NotNull(c.Template);
     Assert.True(visuals.Count>1,name+" drew nothing");
    }
   });
  }
  [Fact]
  public void Button_Renders_Dark_Not_System_Gray(){
   Sta(()=>{
    var b=new Button{Content="Browse",Width=120,Height=40};
    var host=new Border{Background=new SolidColorBrush(Color.FromRgb(0x1B,0x1B,0x1E)),Width=160,Height=80,Child=b};host.Resources.MergedDictionaries.Add(Theme());
    host.Measure(new Size(160,80));host.Arrange(new Rect(0,0,160,80));host.UpdateLayout();
    var bmp=new RenderTargetBitmap(160,80,96,96,PixelFormats.Pbgra32);bmp.Render(host);
    var px=new byte[4];bmp.CopyPixels(new Int32Rect(10,10,1,1),px,4,0); // inside the button, off the text
    Assert.True(px[0]<0x60&&px[1]<0x60&&px[2]<0x60,$"button corner pixel too light: {px[2]:X2}{px[1]:X2}{px[0]:X2}");
   });
  }
 }
}
namespace ZephyrPlayer.Tests {
 public class ThemeGalleryRenderTests {
  [Fact]
  public void Gallery_Of_Shared_Controls_Renders_To_Png(){
   Exception? error=null;var t=new System.Threading.Thread(()=>{
    try{
     var panel=new StackPanel{Margin=new Thickness(24),Width=520};
     panel.Children.Add(new TextBlock{Text="Shared controls",Foreground=Brushes.White,FontSize=18,Margin=new Thickness(0,0,0,12)});
     var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};
     row.Children.Add(new Button{Content="Default button",Margin=new Thickness(0,0,10,0)});
     row.Children.Add(new Button{Content="Accent button",Margin=new Thickness(0,0,10,0)});
     row.Children.Add(new Button{Content="Disabled",IsEnabled=false});
     panel.Children.Add(row);
     ((Button)row.Children[1]).Style=(Style)Theme()["AccentButton"];
     var chips=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,12)};
     foreach(var (label,on) in new[]{("5s",false),("10s",true),("15s",false),("30s",false)})chips.Children.Add(new RadioButton{Content=label,IsChecked=on,GroupName="g"});
     panel.Children.Add(chips);
     panel.Children.Add(new CheckBox{Content="Prevent PC sleep while open",IsChecked=true});
     panel.Children.Add(new CheckBox{Content="Show speed button",IsChecked=false});
     panel.Children.Add(new TextBox{Text="Field text",Margin=new Thickness(0,10,0,10)});
     var list=new ListBox{Height=90};list.Items.Add(new ListBoxItem{Content="Folder A"});list.Items.Add(new ListBoxItem{Content="Folder B",IsSelected=true});list.Items.Add(new ListBoxItem{Content="Movie 1"});
     foreach(ListBoxItem i in list.Items)i.Foreground=Brushes.White;
     panel.Children.Add(list);
     var host=new Border{Background=new SolidColorBrush(Color.FromRgb(0x1B,0x1B,0x1E)),Child=panel,Width=570,Height=440};
     host.Resources.MergedDictionaries.Add(Theme());
     host.Measure(new Size(570,440));host.Arrange(new Rect(0,0,570,440));host.UpdateLayout();
     var bmp=new RenderTargetBitmap(570,440,96,96,PixelFormats.Pbgra32);bmp.Render(host);
     var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bmp));
     using(var f=System.IO.File.Create(System.IO.Path.Combine(Environment.GetEnvironmentVariable("RUNNER_TEMP")??System.IO.Path.GetTempPath(),"pc-theme-gallery.png")))png.Save(f);
    }catch(Exception ex){error=ex;}finally{System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();}
   });t.SetApartmentState(ApartmentState.STA);t.Start();Assert.True(t.Join(60000));Assert.Null(error);
  }
  static ResourceDictionary Theme()=>new ResourceDictionary{Source=new Uri("pack://application:,,,/ZephyrPlayer;component/Theme.xaml")};
 }
}
