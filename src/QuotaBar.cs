using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace CodexQuotaWidget;

public sealed class QuotaBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    static readonly Brush Track = new SolidColorBrush(Color.FromRgb(241, 241, 241));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(237, 148, 231));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(nameof(Sweep), typeof(double), typeof(QuotaBar), new FrameworkPropertyMetadata(-1d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Sweep { get => (double)GetValue(SweepProperty); set => SetValue(SweepProperty, value); }
    public void PlaySweep(bool enabled)
    {
        BeginAnimation(SweepProperty, null); Sweep = -1;
        if (enabled && IsVisible) BeginAnimation(SweepProperty, new DoubleAnimation(-0.2, 1.2, TimeSpan.FromMilliseconds(950)) { FillBehavior = FillBehavior.Stop });
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, ActualWidth, ActualHeight), 3, 3);
        var width = ActualWidth * Math.Clamp(Value, 0, 100) / 100;
        if (width > 0) dc.DrawRoundedRectangle(Accent, null, new Rect(0, 0, width, ActualHeight), 3, 3);
        if (width > 0 && Sweep >= -0.1 && Sweep <= 1.1)
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, ActualHeight), 3, 3));
            var brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 0.5); brush.EndPoint = new Point(1, 0.5);
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5));
            brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            dc.DrawRectangle(brush, null, new Rect(ActualWidth * Sweep - 22, 0, 44, ActualHeight));
            dc.Pop();
        }
    }
}
