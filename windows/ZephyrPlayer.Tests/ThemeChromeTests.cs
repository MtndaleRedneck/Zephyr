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
  static byte[] Pixel(FrameworkElement host,int x,int y){
   host.Measure(new Size(host.Width,host.Height));host.Arrange(new Rect(0,0,host.Width,host.Height));host.UpdateLayout();
   var bmp=new RenderTargetBitmap((int)host.Width,(int)host.Height,96,96,PixelFormats.Pbgra32);bmp.Render(host);
   var px=new byte[4];bmp.CopyPixels(new Int32Rect(x,y,1,1),px,4,0);return px;
  }
  static Border ButtonHost(bool themed){
   var b=new Button{Content="OK",Width=120,Height=40};
   var host=new Border{Background=new SolidColorBrush(Color.FromRgb(0x1B,0x1B,0x1E)),Width=160,Height=80,Child=new Grid{Children={b}}};
   if(themed)host.Resources.MergedDictionaries.Add(Theme());return host;
  }
  [Fact]
  public void Button_Interior_Is_Our_Dark_Fill_And_Not_The_System_Look(){
   Sta(()=>{
    // Button is 120x40 centered in 160x80: it covers x 20..140, y 20..60. Sample inside it, away from the text.
    var themed=Pixel(ButtonHost(true),30,30);
    Assert.True(themed[0]<0x60&&themed[1]<0x60&&themed[2]<0x60,$"themed button interior too light: {themed[2]:X2}{themed[1]:X2}{themed[0]:X2}");
    // Mutation: take the theme away and the same pixel must change, or the test is not looking at the button.
    var plain=Pixel(ButtonHost(false),30,30);
    Assert.NotEqual(themed,plain);
    // And the host background outside the button is the dark host color, not the button.
    var outside=Pixel(ButtonHost(true),5,5);Assert.Equal(0x1B,outside[2]);
   });
  }
  [Fact]
  public void MenuItem_Shows_Whether_It_Is_Checked(){
   Sta(()=>{
    foreach(var on in new[]{true,false}){
     var menu=new ContextMenu();var item=new MenuItem{Header="Name",IsCheckable=true,IsChecked=on};menu.Items.Add(item);
     menu.Resources.MergedDictionaries.Add(Theme());menu.ApplyTemplate();menu.Measure(new Size(240,200));menu.Arrange(new Rect(0,0,240,200));menu.UpdateLayout();
     item.ApplyTemplate();var check=(UIElement)item.Template.FindName("Check",item);
     Assert.Equal(on?Visibility.Visible:Visibility.Hidden,check.Visibility); // Hidden keeps the gutter so checked and unchecked labels align
     Assert.True(((FrameworkElement)check).Width+((FrameworkElement)check).Margin.Right>0);
    }
   });
  }
  [Fact]
  public void MenuItem_Labels_Align_Whether_Or_Not_Checked(){
   Sta(()=>{
    double X(bool on){
     var menu=new ContextMenu();var item=new MenuItem{Header="Name",IsCheckable=true,IsChecked=on};menu.Items.Add(item);
     menu.Resources.MergedDictionaries.Add(Theme());menu.ApplyTemplate();menu.Measure(new Size(240,200));menu.Arrange(new Rect(0,0,240,200));menu.UpdateLayout();
     item.ApplyTemplate();var cp=(FrameworkElement)item.Template.FindName("Bd",item);
     var label=FindFirst<ContentPresenter>(cp)!;return label.TranslatePoint(new Point(0,0),item).X;
    }
    Assert.Equal(X(true),X(false),1);
    Assert.True(X(false)>10,"label starts at the check gutter, not at the edge");
   });
  }
  static T? FindFirst<T>(DependencyObject o) where T:DependencyObject{
   for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++){var c=VisualTreeHelper.GetChild(o,i);if(c is T t)return t;var r=FindFirst<T>(c);if(r!=null)return r;}
   return null;
  }
  [Fact]
  public void SubMenu_Template_Has_Popup_ItemsPresenter_And_Arrow(){
   Sta(()=>{
    var menu=new ContextMenu();var parent=new MenuItem{Header="Speed"};parent.Items.Add(new MenuItem{Header="1x"});menu.Items.Add(parent);
    menu.Resources.MergedDictionaries.Add(Theme());menu.ApplyTemplate();menu.Measure(new Size(240,200));menu.Arrange(new Rect(0,0,240,200));menu.UpdateLayout();
    parent.ApplyTemplate();
    Assert.Equal(MenuItemRole.SubmenuHeader,parent.Role);
    var popup=(Popup)parent.Template.FindName("PART_Popup",parent);Assert.NotNull(popup);
    Assert.NotNull(FindFirst<ItemsPresenter>(popup.Child));
    Assert.Equal(Visibility.Visible,((UIElement)parent.Template.FindName("Arrow",parent)).Visibility);
    parent.IsSubmenuOpen=false;Assert.False(popup.IsOpen);
   });
  }
  [Fact]
  public void ListBoxItem_Style_Has_A_Keyboard_Focus_Trigger_Structural_Only(){
   Sta(()=>{
    // Structural check only: real keyboard focus cannot be driven offscreen. The ring is the 2px orange border.
    var host=new Border{Child=new ListBox{Items={new ListBoxItem{Content="a"}}}};host.Resources.MergedDictionaries.Add(Theme());
    host.Measure(new Size(200,100));host.Arrange(new Rect(0,0,200,100));host.UpdateLayout();
    var item=(ListBoxItem)((ListBox)host.Child).Items[0]!;
    var style=item.Style??(Style)host.Resources[typeof(ListBoxItem)];Assert.NotNull(style);
    var triggers=new List<TriggerBase>(style.Triggers);
    Assert.Contains(triggers,t=>t is Trigger tr&&tr.Property==UIElement.IsKeyboardFocusedProperty);
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
