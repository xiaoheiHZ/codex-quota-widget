using System;
using System.Windows;
using System.Windows.Media;

namespace CodexQuotaWidget;

public sealed class ArcGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(ArcGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    static readonly Pen Track = new(new SolidColorBrush(Color.FromRgb(241, 241, 241)), 5);
    static readonly Pen Accent = new(new SolidColorBrush(Color.FromRgb(237, 148, 231)), 5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
    protected override void OnRender(DrawingContext dc)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Min(ActualWidth, ActualHeight) / 2 - 5;
        if (radius <= 0) return;
        dc.DrawEllipse(null, Track, center, radius, radius);
        var value = Math.Clamp(Value, 0, 100);
        if (value >= 99.999) dc.DrawEllipse(null, Accent, center, radius, radius);
        else if (value > 0)
        {
            var angle = value / 100 * 2 * Math.PI - Math.PI / 2;
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(center.X, center.Y - radius), false, false);
                g.ArcTo(new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)), new Size(radius, radius), 0, value > 50, SweepDirection.Clockwise, true, false);
            }
            dc.DrawGeometry(null, Accent, geometry);
        }
    }
}
