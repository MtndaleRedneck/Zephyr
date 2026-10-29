using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace ZephyrPlayer;

/// <summary>The approved broad tail-to-tip highlight. Only the arrow changes;
/// no label rotates, and the selected orange/white base determines the inverse.</summary>
internal sealed class SecondarySkipHighlight : FrameworkElement
{
    internal static readonly DependencyProperty ProgressProperty=DependencyProperty.Register(nameof(Progress),typeof(double),typeof(SecondarySkipHighlight),new FrameworkPropertyMetadata(-2d,FrameworkPropertyMetadataOptions.AffectsRender));
    public double Progress {get=>(double)GetValue(ProgressProperty);set=>SetValue(ProgressProperty,value);}
    internal bool Backward {get;set;}
    internal Color BaseColor {get;set;}=Color.FromRgb(245,245,247);
    internal static Color HighlightColor(Color baseColor)=>baseColor==Brand.Orange?Colors.White:Brand.Orange;
    internal void Start(Color baseColor)
    {
        BaseColor=baseColor;BeginAnimation(ProgressProperty,null);Progress=-2;
        BeginAnimation(ProgressProperty,new DoubleAnimation(0,1,TimeSpan.FromMilliseconds(500)){FillBehavior=FillBehavior.Stop});
    }
    internal void Stop(){BeginAnimation(ProgressProperty,null);Progress=-2;InvalidateVisual();}
    internal void Preview(Color color,double progress){Stop();BaseColor=color;Progress=progress;}
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var progress=Progress;if(progress<0||progress>1)return;
        const int segments=48;
        Point At(double q){var x=8+38*q;var y=17*(1-q)*(1-q)-4*(1-q)*q+17*q*q;return new Point(Backward?54-x:x,y);}
        var highlight=HighlightColor(BaseColor);
        for(var i=0;i<segments;i++){
            var distance=Math.Abs((i+.5)/segments-progress);if(distance>=.36)continue;
            var amount=Math.Min(1,1.65*(1-distance/.36));
            var color=Color.FromRgb((byte)(BaseColor.R+(highlight.R-BaseColor.R)*amount),(byte)(BaseColor.G+(highlight.G-BaseColor.G)*amount),(byte)(BaseColor.B+(highlight.B-BaseColor.B)*amount));
            var pen=new Pen(new SolidColorBrush(color),2.6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            dc.DrawLine(pen,At((double)i/segments),At((double)(i+1)/segments));
        }
        if(progress>.69){var pen=new Pen(new SolidColorBrush(highlight),2.6){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};var tip=At(1);dc.DrawLine(pen,tip,new Point(Backward?8:46,8));dc.DrawLine(pen,tip,new Point(Backward?17:37,14));}
    }
}
