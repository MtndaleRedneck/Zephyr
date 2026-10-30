using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
namespace ZephyrPlayer;

/// <summary>The approved broad tail-to-tip highlight on the secondary (30s) arrow:
/// a light runs along the arc from its tail to the arrow head (render 02 shows the
/// forward one mid-travel). Drawn in the icon's own 24-unit space (it sits in the
/// icon's Canvas, inside the same Viewbox). Only the arrow changes; the label never
/// moves, and the selected orange/white base determines the inverse colour.</summary>
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

    // The arc of render 02: radius 8.8 about (12, 13.40), from 20 deg to 160 deg above
    // the horizontal (y grows downward). Backward runs right -> left, forward left -> right.
    internal const double Cx=12, Cy=13.40, Radius=8.8, A0=-20, Sweep=-140;
    internal Point At(double q)
    {
        var deg=Backward ? A0+Sweep*q : (180-A0)-Sweep*q;
        var rad=deg*Math.PI/180;
        return new Point(Cx+Radius*Math.Cos(rad),Cy+Radius*Math.Sin(rad));
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);var progress=Progress;if(progress<0||progress>1)return;
        const int segments=48;
        var highlight=HighlightColor(BaseColor);
        for(var i=0;i<segments;i++){
            var distance=Math.Abs((i+.5)/segments-progress);if(distance>=.36)continue;
            var amount=Math.Min(1,1.65*(1-distance/.36));
            var color=Color.FromRgb((byte)(BaseColor.R+(highlight.R-BaseColor.R)*amount),(byte)(BaseColor.G+(highlight.G-BaseColor.G)*amount),(byte)(BaseColor.B+(highlight.B-BaseColor.B)*amount));
            var pen=new Pen(new SolidColorBrush(color),2.3){StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
            dc.DrawLine(pen,At((double)i/segments),At((double)(i+1)/segments));
        }
        if(progress>.69){
            // The light reaches the head: tint the arrow head too.
            var head=new StreamGeometry();
            using(var g=head.Open()){
                if(Backward){g.BeginFigure(new Point(2.92,12.62),true,true);g.LineTo(new Point(1.95,9.08),true,false);g.LineTo(new Point(5.94,10.53),true,false);}
                else{g.BeginFigure(new Point(21.08,12.62),true,true);g.LineTo(new Point(18.06,10.53),true,false);g.LineTo(new Point(22.05,9.08),true,false);}
            }
            head.Freeze();
            dc.DrawGeometry(new SolidColorBrush(highlight),new Pen(new SolidColorBrush(highlight),0.8){LineJoin=PenLineJoin.Round},head);
        }
    }
}
