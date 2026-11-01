using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexQuotaWidget;

/// <summary>Only changing glyphs roll. Subscribes to frame events during a transition, never while idle.</summary>
public sealed class RollingText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RollingText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender, TextChanged));
    public static readonly DependencyProperty MotionProperty = DependencyProperty.Register(nameof(Motion), typeof(bool), typeof(RollingText),
        new PropertyMetadata(true, (d, e) => { if (!(bool)e.NewValue) ((RollingText)d).Stop(); }));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool Motion { get => (bool)GetValue(MotionProperty); set => SetValue(MotionProperty, value); }
    public bool IsAnimating => rendering;
    string previous = "";
    long started;
    bool rendering;
    double progress = 1;
    public RollingText()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Unloaded += (_, _) => Stop();
        IsVisibleChanged += (_, _) => { if (!IsVisible) Stop(); };
    }
    static void TextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var self = (RollingText)sender;
        self.previous = (string?)e.OldValue ?? "";
        if (!self.Motion || !SystemParameters.ClientAreaAnimation || !self.IsVisible || self.previous.Length == 0) { self.Stop(); return; }
        self.started = Stopwatch.GetTimestamp(); self.progress = 0;
        if (!self.rendering) { CompositionTarget.Rendering += self.Frame; self.rendering = true; }
    }
    void Frame(object? sender, EventArgs args)
    {
        progress = Math.Min(1, Stopwatch.GetElapsedTime(started).TotalMilliseconds / 290);
        InvalidateVisual();
        if (progress >= 1) Stop();
    }
    void Stop()
    {
        if (rendering) { CompositionTarget.Rendering -= Frame; rendering = false; }
        progress = 1; previous = Text; InvalidateVisual();
    }
    FormattedText Format(string value) => new(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    protected override Size MeasureOverride(Size availableSize)
    {
        var text = Format(string.IsNullOrEmpty(Text) ? " " : Text);
        return new Size(text.WidthIncludingTrailingWhitespace, text.Height);
    }
    protected override void OnRender(DrawingContext dc)
    {
        var text = Text ?? "";
        if (progress >= 1 || text.Length != previous.Length)
        {
            dc.DrawText(Format(text), new Point(0, 0)); return;
        }
        var eased = 1 - Math.Pow(1 - progress, 3);
        double x = 0;
        for (int i = 0; i < text.Length; i++)
        {
            var glyph = Format(text[i].ToString());
            if (text[i] == previous[i]) dc.DrawText(glyph, new Point(x, 0));
            else
            {
                var width = Math.Max(glyph.WidthIncludingTrailingWhitespace, Format(previous[i].ToString()).WidthIncludingTrailingWhitespace);
                dc.PushClip(new RectangleGeometry(new Rect(x, 0, width + 0.5, ActualHeight)));
                dc.PushOpacity(1 - eased);
                dc.DrawText(Format(previous[i].ToString()), new Point(x, -eased * ActualHeight * 0.75));
                dc.Pop(); dc.PushOpacity(eased);
                dc.DrawText(glyph, new Point(x, (1 - eased) * ActualHeight * 0.75));
                dc.Pop(); dc.Pop();
            }
            x += glyph.WidthIncludingTrailingWhitespace;
        }
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new RollingTextPeer(this);
    sealed class RollingTextPeer(RollingText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetNameCore() => owner.Text;
        protected override string GetClassNameCore() => nameof(RollingText);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    }
}
