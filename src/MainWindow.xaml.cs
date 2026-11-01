using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace CodexQuotaWidget;

public partial class MainWindow : Window
{
    readonly WidgetSettings settings;
    readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer pollTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    readonly CancellationTokenSource lifetime = new();
    readonly bool isolated = App.Demo || App.Fixture != null || App.CapturePath != null || App.MotionCheckPath != null;
    CodexClient? client;
    Forms.NotifyIcon? tray;
    QuotaSnapshot? snapshot;
    QuotaBucket? bucket;
    QuotaWindow? primary;
    QuotaWindow? secondary;
    DateTimeOffset nextResetProbe;
    bool busy, syncingPicker, failed, closed;
    int clockTicks, countdownChanges;
    double displayedValue;
    string? animatedWindowKey;
    string? animatedSecondaryKey;
    string? lastCountdown;
    AnimationClock? numberClock;
    ContextMenu? menu;
    int transitionGeneration;

    public MainWindow()
    {
        InitializeComponent();
        settings = isolated ? new WidgetSettings() : WidgetSettings.Load();
        settings.Style = WidgetStyles.Resolve(App.InitialStyle ?? settings.Style).Id;
        settings.MotionLevel = settings.MotionLevel == "soft" ? "soft" : "fluid";
        if (App.NoMotion) settings.Motion = false;
        Width = PresentationWidth;
        ApplyPresentation();
        Topmost = settings.Topmost;
        PinButton.Foreground = new SolidColorBrush(Topmost ? Color.FromRgb(187, 111, 179) : Color.FromRgb(153, 143, 157));
        clockTimer.Tick += ClockTick;
        pollTimer.Tick += async (_, _) => await RefreshAsync();
        IsVisibleChanged += (_, _) => ApplyMotion();
        SystemParameters.StaticPropertyChanged += SystemMotionChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
        {
            if (message == WindowSignal.ShowMessage) { ShowWidget(); handled = true; }
            return IntPtr.Zero;
        });
    }

    async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        RestorePosition();
        if (!isolated) CreateTray();
        ApplyMotion();
        if (MotionEnabled)
        {
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(480)));
            EntranceTransform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = Ease() });
        }
        clockTimer.Start();
        if (App.Fixture != null) LoadFixture(App.Fixture);
        else if (App.Demo) ApplySnapshot(DemoSnapshot());
        else { pollTimer.Start(); await RefreshAsync(); }
        if (App.MotionCheckPath != null)
        {
            await CheckMotionAsync(App.MotionCheckPath); Close(); return;
        }
        if (App.CapturePath != null)
        {
            await Task.Delay(1800);
            UpdateLayout();
            SaveCapture(App.CapturePath);
            Close();
        }
    }

    bool MotionEnabled => settings.Motion && SystemParameters.ClientAreaAnimation && IsVisible;
    bool FluidMotion => MotionEnabled && settings.MotionLevel != "soft";
    double PresentationWidth => settings.Style == "strip" && settings.Compact ? 356 : WidgetStyles.Resolve(settings.Style).Width;
    static CubicEase Ease() => new() { EasingMode = EasingMode.EaseOut };

    void SystemMotionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Dispatcher.BeginInvoke(ApplyMotion);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    void ApplyPresentation()
    {
        PanelView.Visibility = settings.Style == "panel" ? Visibility.Visible : Visibility.Collapsed;
        StripView.Visibility = settings.Style == "strip" ? Visibility.Visible : Visibility.Collapsed;
        ClockView.Visibility = settings.Style == "clock" ? Visibility.Visible : Visibility.Collapsed;
        OrbitView.Visibility = settings.Style == "orbit" ? Visibility.Visible : Visibility.Collapsed;
        var showSecondary = secondary != null && !settings.Compact;
        foreach (var view in new FrameworkElement[] { SecondaryCard, StripSecondary, StripDivider, ClockSecondary, OrbitSecondary })
            view.Visibility = showSecondary ? Visibility.Visible : Visibility.Collapsed;
        StripDividerColumn.Width = new GridLength(showSecondary ? 37 : 0);
        StripSecondColumn.Width = showSecondary ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        OrbitColumns.Columns = showSecondary ? 2 : 1;
        foreach (var label in new FrameworkElement[] { ResetText, ClockResetLabel, OrbitResetLabel })
            label.Visibility = settings.Compact ? Visibility.Collapsed : Visibility.Visible;
        StyleButton.ToolTip = WidgetStyles.Resolve(settings.Style).Name + " · 点击切换 · Ctrl+Tab";
    }

    void CancelTransition()
    {
        transitionGeneration++;
        BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null); BeginAnimation(LeftProperty, null);
        Width = PresentationWidth; Height = double.NaN;
        Card.BeginAnimation(OpacityProperty, null); EntranceTransform.BeginAnimation(TranslateTransform.XProperty, null);
        EntranceTransform.BeginAnimation(TranslateTransform.YProperty, null);
        Card.Opacity = 1; EntranceTransform.X = 0; EntranceTransform.Y = 0;
        TransitionGhost.Visibility = Visibility.Collapsed; TransitionGhost.Source = null;
        SizeToContent = SizeToContent.Height;
        if (IsLoaded) UpdateLayout();
    }

    void ChangePresentation(string style)
    {
        CancelTransition();
        var oldWidth = ActualWidth; var oldHeight = ActualHeight;
        var oldLeft = Left;
        var rightEdge = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var anchorRight = rightEdge - (Left + ActualWidth) is >= 0 and <= 56;
        BitmapSource? oldImage = null;
        if (MotionEnabled && Card.ActualWidth > 0 && Card.ActualHeight > 0) oldImage = RenderVisual(Card);
        var oldCardSize = new Size(Card.ActualWidth, Card.ActualHeight);
        settings.Style = WidgetStyles.Resolve(style).Id;
        ApplyPresentation();
        Width = PresentationWidth;
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        var targetWidth = Width; var targetHeight = ActualHeight;
        var targetLeft = anchorRight ? Math.Max(SystemParameters.VirtualScreenLeft, oldLeft + oldWidth - targetWidth) : oldLeft;
        Left = targetLeft;
        if (!isolated) settings.Save();
        if (oldImage == null) { KeepOnDesktop(); return; }
        var generation = transitionGeneration;
        SizeToContent = SizeToContent.Manual;
        Width = targetWidth; Height = targetHeight;
        TransitionGhost.Source = oldImage;
        TransitionGhost.Width = oldCardSize.Width; TransitionGhost.Height = oldCardSize.Height;
        TransitionGhost.Visibility = Visibility.Visible;
        GhostTransform.X = 0;
        TransitionGhost.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180)));
        if (FluidMotion) GhostTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -9, TimeSpan.FromMilliseconds(210)) { EasingFunction = Ease() });
        IEasingFunction easing = FluidMotion ? new BackEase { Amplitude = 0.08, EasingMode = EasingMode.EaseOut } : Ease();
        var duration = TimeSpan.FromMilliseconds(FluidMotion ? 460 : 300);
        BeginAnimation(WidthProperty, new DoubleAnimation(oldWidth, targetWidth, duration) { EasingFunction = easing });
        if (Math.Abs(oldLeft - targetLeft) > 0.1) BeginAnimation(LeftProperty, new DoubleAnimation(oldLeft, targetLeft, duration) { EasingFunction = easing });
        var resize = new DoubleAnimation(oldHeight, targetHeight, duration) { EasingFunction = easing };
        resize.Completed += (_, _) =>
        {
            if (generation != transitionGeneration || closed) return;
            BeginAnimation(WidthProperty, null); BeginAnimation(HeightProperty, null); BeginAnimation(LeftProperty, null);
            Width = targetWidth; Height = double.NaN;
            TransitionGhost.Visibility = Visibility.Collapsed; TransitionGhost.Source = null;
            SizeToContent = SizeToContent.Height;
            KeepOnDesktop();
        };
        BeginAnimation(HeightProperty, resize);
        Card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { BeginTime = TimeSpan.FromMilliseconds(70) });
        if (FluidMotion) EntranceTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(10, 0, duration) { EasingFunction = Ease() });
        ApplyMotion();
        SweepBars();
    }

    void KeepOnDesktop()
    {
        var right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
        var bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, SystemParameters.VirtualScreenLeft, Math.Max(SystemParameters.VirtualScreenLeft, right - ActualWidth));
        Top = Math.Clamp(Top, SystemParameters.VirtualScreenTop, Math.Max(SystemParameters.VirtualScreenTop, bottom - ActualHeight));
        SavePosition();
    }

    static RenderTargetBitmap RenderVisual(FrameworkElement visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    void SweepBars()
    {
        foreach (var bar in Descendants<QuotaBar>(Card)) bar.PlaySweep(FluidMotion);
    }

    async Task RefreshAsync()
    {
        if (busy || closed || App.Demo || App.Fixture != null) return;
        busy = true; RefreshButton.IsEnabled = false;
        if (snapshot == null) StatusLabel.Text = "正在连接";
        StartRefreshSpin();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (client == null)
            {
                client = new CodexClient();
                await client.StartAsync(timeout.Token);
            }
            var fresh = await client.ReadQuotaAsync(timeout.Token);
            if (closed) return;
            failed = false;
            ApplySnapshot(fresh);
            SweepBars();
            if (MotionEnabled) LiveDot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(600)));
        }
        catch (OperationCanceledException) when (closed) { }
        catch (Exception ex)
        {
            if (!closed)
            {
                failed = true;
                if (snapshot == null) ShowEmpty(ex is QuotaReadException q ? q.Message : "暂时无法同步",
                    ex is QuotaReadException qr ? qr.Hint : "请检查网络与 Codex 登录状态，稍后会自动重试。");
                else
                {
                    StatusLabel.Text = "同步中断 · 上次数据";
                    WarningCard.Visibility = Visibility.Visible;
                    WarningText.Text = "同步中断，显示上次读取的额度；恢复连接后自动更新。";
                    LiveDot.Fill = new SolidColorBrush(Color.FromRgb(199, 175, 144));
                }
                if (client != null) { await client.DisposeAsync(); client = null; }
                UpdateClock();
            }
        }
        finally
        {
            busy = false;
            RefreshButton.IsEnabled = true;
            ((RotateTransform)RefreshIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }

    void ApplySnapshot(QuotaSnapshot fresh)
    {
        snapshot = fresh;
        syncingPicker = true;
        BucketPicker.ItemsSource = snapshot.Buckets;
        bucket = snapshot.Buckets.FirstOrDefault(b => b.Id == settings.SelectedBucket) ?? snapshot.Buckets.FirstOrDefault();
        BucketPicker.SelectedItem = bucket;
        BucketPicker.Visibility = snapshot.Buckets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        syncingPicker = false;
        RenderBucket();
    }

    void RenderBucket()
    {
        if (bucket == null) { ShowEmpty("暂无额度数据", "Codex 尚未返回额度信息。请确认已登录 ChatGPT 账号。"); return; }
        settings.SelectedBucket = bucket.Id;
        if (!isolated) settings.Save();
        PlanLabel.Text = bucket.PlanLabel;
        primary = bucket.Windows.FirstOrDefault();
        secondary = bucket.Windows.Skip(1).FirstOrDefault();
        if (primary == null)
        {
            ShowEmpty("暂无额度窗口", bucket.UnlimitedCredits ? "当前账号返回无限积分，未提供百分比额度窗口。"
                : bucket.CreditBalance is { } balance ? $"当前积分余额：{balance}。账号未提供百分比额度窗口。"
                : "账号未提供百分比额度窗口。请稍后刷新。");
            return;
        }
        QuotaContent.Visibility = Visibility.Visible; EmptyContent.Visibility = Visibility.Collapsed;
        HeroLabel.Text = primary.Label;
        SecondaryLabel.Text = secondary?.Label ?? "";
        ApplyPresentation();
        WarningCard.Visibility = snapshot?.OrdinaryUsageAllowed == false ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot?.OrdinaryUsageAllowed == false) WarningText.Text = "账号当前受使用限制，实际可用情况以 Codex 为准。";
        StatusLabel.Text = App.Demo || App.Fixture != null ? "演示数据" : "已同步";
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(217, 124, 209));
        UpdateClock();
    }

    void ShowEmpty(string title, string hint)
    {
        primary = secondary = null;
        QuotaContent.Visibility = Visibility.Collapsed; EmptyContent.Visibility = Visibility.Visible;
        EmptyTitle.Text = title; EmptyHint.Text = hint;
        StatusLabel.Text = failed ? "等待连接" : "暂无数据";
        WarningCard.Visibility = Visibility.Collapsed;
        LiveDot.Fill = new SolidColorBrush(Color.FromRgb(194, 175, 191));
        if (snapshot == null) UpdatedText.Text = "每分钟自动重试 · 也可点击刷新";
    }

    void ClockTick(object? sender, EventArgs e)
    {
        clockTicks++;
        UpdateClock();
        if (closed || !IsVisible) return;
        var now = DateTimeOffset.UtcNow;
        if (snapshot != null && snapshot.Buckets.SelectMany(b => b.Windows).Any(w => w.ResetDue(now)) && now >= nextResetProbe)
        {
            nextResetProbe = now.AddSeconds(15);
            _ = RefreshAsync();
        }
    }

    void UpdateClock()
    {
        var now = DateTimeOffset.UtcNow;
        if (primary != null)
        {
            var due = primary.ResetDue(now);
            var key = $"{bucket?.Id}|{primary.DurationMinutes}|{primary.UsedPercent}|{primary.ResetsAt}|{due}";
            if (animatedWindowKey != key)
            {
                animatedWindowKey = key;
                if (due)
                {
                    numberClock?.Controller?.Remove(); numberClock = null;
                    PercentText.Text = "—"; PercentSign.Visibility = Visibility.Collapsed;
                    Gauge.BeginAnimation(QuotaBar.ValueProperty, null); Gauge.Value = 0; displayedValue = 0;
                }
                else
                {
                    PercentSign.Visibility = Visibility.Visible;
                    AnimateQuota(primary.RemainingPercent);
                }
            }
            CountdownText.Text = Countdown.Format(primary.ResetsAt, now);
            ResetText.Text = primary.ResetsAt != null ? primary.ExactReset : "服务尚未提供重置时间";
            if (lastCountdown != CountdownText.Text)
            {
                if (MotionEnabled && !FluidMotion) CountdownText.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1, TimeSpan.FromMilliseconds(180)));
                lastCountdown = CountdownText.Text;
                countdownChanges++;
            }
        }
        if (secondary != null)
        {
            var due = secondary.ResetDue(now);
            SecondaryPercent.Text = due ? "待确认" : $"{secondary.RemainingPercent:0.#}%";
            SecondaryCountdown.Text = due ? "正在确认重置" : secondary.ResetsAt == null ? "重置时间待提供" : Countdown.Format(secondary.ResetsAt, now) + " 后重置";
            SecondaryCard.ToolTip = secondary.ResetsAt != null ? secondary.ExactReset : "服务尚未提供重置时间";
            var secondaryKey = $"{bucket?.Id}|{secondary.UsedPercent}|{due}|{settings.Compact}";
            if (animatedSecondaryKey != secondaryKey)
            {
                animatedSecondaryKey = secondaryKey;
                var value = due ? 0 : secondary.RemainingPercent;
                var from = SecondaryBar.Value;
                SecondaryBar.BeginAnimation(QuotaBar.ValueProperty, null); SecondaryBar.Value = value;
                if (MotionEnabled) SecondaryBar.BeginAnimation(QuotaBar.ValueProperty, new DoubleAnimation(from, value, TimeSpan.FromMilliseconds(750)) { EasingFunction = Ease() });
            }
        }
        if (snapshot != null)
        {
            var age = Math.Max(0, (now - snapshot.SampledAt).TotalSeconds);
            UpdatedText.Text = App.Demo || App.Fixture != null ? "非真实额度"
                : age < 10 ? "刚刚更新" : age < 60 ? $"{(int)age} 秒前更新" : $"{(int)(age / 60)} 分钟前更新";
            UpdatedText.ToolTip = "上次同步：" + snapshot.SampledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        }
    }

    void AnimateQuota(double target)
    {
        numberClock?.Controller?.Remove();
        if (!MotionEnabled) { Gauge.BeginAnimation(QuotaBar.ValueProperty, null); Gauge.Value = target; displayedValue = target; PercentText.Text = target.ToString("0.#"); return; }
        var animation = new DoubleAnimation(displayedValue, target, TimeSpan.FromMilliseconds(650)) { EasingFunction = Ease() };
        var clock = animation.CreateClock(); numberClock = clock;
        clock.CurrentTimeInvalidated += (_, _) =>
        {
            if (numberClock != clock || clock.CurrentProgress == null) return;
            displayedValue = animation.GetCurrentValue(animation.From!.Value, target, clock);
            PercentText.Text = displayedValue.ToString(clock.CurrentProgress >= 1 ? "0.#" : "0", CultureInfo.InvariantCulture);
        };
        Gauge.ApplyAnimationClock(QuotaBar.ValueProperty, clock);
    }

    void ApplyMotion()
    {
        foreach (var text in Descendants<RollingText>(Card)) text.Motion = FluidMotion;
        if (!MotionEnabled)
        {
            CancelTransition();
            CountdownText.BeginAnimation(OpacityProperty, null);
            Card.BeginAnimation(OpacityProperty, null); EntranceTransform.BeginAnimation(TranslateTransform.YProperty, null);
            if (primary != null && !primary.ResetDue(DateTimeOffset.UtcNow)) AnimateQuota(primary.RemainingPercent);
            if (secondary != null) { SecondaryBar.BeginAnimation(QuotaBar.ValueProperty, null); SecondaryBar.Value = secondary.ResetDue(DateTimeOffset.UtcNow) ? 0 : secondary.RemainingPercent; }
            SweepBars();
        }
    }

    void CardEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (MotionEnabled) CardShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            new DoubleAnimation(0.14, TimeSpan.FromMilliseconds(220)));
    }
    void CardLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        CardShadow.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.OpacityProperty,
            MotionEnabled ? new DoubleAnimation(0.09, TimeSpan.FromMilliseconds(280)) : null);
    }

    void StartRefreshSpin()
    {
        if (MotionEnabled) ((RotateTransform)RefreshIcon.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(850)) { RepeatBehavior = RepeatBehavior.Forever });
    }

    void RestorePosition()
    {
        var area = SystemParameters.WorkArea;
        Left = settings.Left is { } left && double.IsFinite(left) ? left : area.Right - Width - 18;
        Top = settings.Top is { } top && double.IsFinite(top) ? top : area.Top + 60;
        // A saved monitor may be disconnected: bring the widget back into the available desktop.
        if (Left + Width < SystemParameters.VirtualScreenLeft + 60 || Left > SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 ||
            Top + ActualHeight < SystemParameters.VirtualScreenTop + 60 || Top > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60)
        { Left = area.Right - Width - 18; Top = area.Top + 60; }
    }

    void SavePosition()
    {
        if (isolated) return;
        settings.Left = Left; settings.Top = Top; settings.Save();
    }

    void CreateTray()
    {
        var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/widget.ico"))!.Stream;
        using (stream)
        using (var icon = new System.Drawing.Icon(stream)) tray = new Forms.NotifyIcon { Icon = (System.Drawing.Icon)icon.Clone(), Text = "Codex 额度", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWidget);
        tray.MouseUp += (_, e) => { if (e.Button == Forms.MouseButtons.Right) Dispatcher.Invoke(() => OpenMenu(true)); };
    }

    void ShowWidget()
    {
        Show(); WindowState = WindowState.Normal; Activate(); ApplyMotion(); UpdateClock();
        if (snapshot == null || DateTimeOffset.UtcNow - snapshot.SampledAt > TimeSpan.FromSeconds(60)) _ = RefreshAsync();
    }

    void OpenMenu(bool atPointer = false)
    {
        menu = (ContextMenu)FindResource("WidgetMenu");
        UpdateMenuChecks(menu.Items);
        foreach (var item in menu.Items.OfType<MenuItem>())
            if ((string?)item.Tag == "show") item.Visibility = atPointer && !IsVisible ? Visibility.Visible : Visibility.Collapsed;
        menu.PlacementTarget = Header;
        menu.Placement = atPointer ? System.Windows.Controls.Primitives.PlacementMode.MousePoint : System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.HorizontalOffset = atPointer ? 0 : 80;
        menu.IsOpen = true;
    }

    void UpdateMenuChecks(ItemCollection items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            var tag = item.Tag as string;
            item.IsChecked = tag switch
            {
                "pin" => Topmost, "compact" => settings.Compact, "motion" => settings.Motion,
                "startup" => WidgetSettings.AutoStart,
                _ when tag?.StartsWith("style:") == true => tag[6..] == settings.Style,
                _ when tag?.StartsWith("motion:") == true => tag[7..] == settings.MotionLevel,
                _ => false
            };
            UpdateMenuChecks(item.Items);
        }
    }

    void StyleClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }) ChangePresentation(tag[6..]);
    }
    void CycleStyleClick(object sender, RoutedEventArgs e)
    {
        var index = Array.FindIndex(WidgetStyles.All, style => style.Id == settings.Style);
        ChangePresentation(WidgetStyles.All[(index + 1) % WidgetStyles.All.Length].Id);
    }
    void MotionLevelClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        settings.MotionLevel = tag[7..]; settings.Motion = true; ApplyMotion();
        if (!isolated) settings.Save();
        SweepBars();
    }

    void ShowClick(object sender, RoutedEventArgs e) => ShowWidget();
    void FontLicenseClick(object sender, RoutedEventArgs e)
    {
        using var stream = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Fonts/LICENSE.txt"))!.Stream;
        using var reader = new StreamReader(stream);
        var text = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(20),
            FontFamily = (FontFamily)FindResource("WidgetFont"), FontWeight = FontWeights.Medium, FontSize = 12 };
        new Window { Title = "Source Han Sans · 字体许可", Width = 560, Height = 500, Owner = this, Content = text, WindowStartupLocation = WindowStartupLocation.CenterOwner }.ShowDialog();
    }

    void DragCard(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        var source = e.OriginalSource as DependencyObject;
        while (source != null && source != Card)
        {
            if (source is Button or ComboBox) return;
            source = VisualTreeHelper.GetParent(source);
        }
        try { DragMove(); SavePosition(); } catch (InvalidOperationException) { }
    }
    void CardRightClick(object sender, MouseButtonEventArgs e) { OpenMenu(); e.Handled = true; }
    void MenuClick(object sender, RoutedEventArgs e) => OpenMenu();
    async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    void PinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost; settings.Topmost = Topmost;
        PinButton.Foreground = new SolidColorBrush(Topmost ? Color.FromRgb(187, 111, 179) : Color.FromRgb(153, 143, 157));
        if (!isolated) settings.Save();
    }
    void CompactClick(object sender, RoutedEventArgs e)
    {
        settings.Compact = !settings.Compact; ChangePresentation(settings.Style);
        if (!isolated) settings.Save();
    }
    void MotionClick(object sender, RoutedEventArgs e)
    {
        settings.Motion = !settings.Motion; ApplyMotion();
        if (!isolated) settings.Save();
    }
    void StartupClick(object sender, RoutedEventArgs e)
    {
        if (isolated) return;
        try { WidgetSettings.SetAutoStart(!WidgetSettings.AutoStart); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { WarningCard.Visibility = Visibility.Visible; WarningText.Text = "未能修改开机启动设置。"; }
    }
    void HideClick(object sender, RoutedEventArgs e) { Hide(); ApplyMotion(); }
    void ExitClick(object sender, RoutedEventArgs e) => Close();
    void BucketChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingPicker || BucketPicker.SelectedItem is not QuotaBucket selected) return;
        bucket = selected; RenderBucket();
    }
    void WindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.F5) { _ = RefreshAsync(); e.Handled = true; }
        if (e.Key == Key.Tab && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { CycleStyleClick(this, e); e.Handled = true; }
        if (e.Key == Key.Escape && !isolated) { Hide(); ApplyMotion(); e.Handled = true; }
    }
    async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        closed = true; SavePosition(); clockTimer.Stop(); pollTimer.Stop(); lifetime.Cancel();
        SystemParameters.StaticPropertyChanged -= SystemMotionChanged;
        tray?.Dispose(); tray = null;
        if (client != null) await client.DisposeAsync();
    }

    void SaveCapture(string path)
    {
        foreach (var text in Descendants<RollingText>(Card)) text.Motion = false;
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using (var file = File.Create(path)) encoder.Save(file);
        var font = new Typeface((FontFamily)FindResource("WidgetFont"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        font.TryGetGlyphTypeface(out var resolvedFont);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new
        {
            width = ActualWidth, height = ActualHeight, percent = PercentText.Text,
            countdown = CountdownText.Text, secondary = SecondaryPercent.Text, status = StatusLabel.Text,
            primaryVisible = QuotaContent.Visibility == Visibility.Visible,
            secondaryVisible = secondary != null && !settings.Compact && QuotaContent.Visibility == Visibility.Visible,
            emptyTitle = EmptyTitle.Text, clockTicks, countdownChanges,
            style = settings.Style, motion = settings.Motion, motionLevel = settings.MotionLevel,
            fontFile = resolvedFont?.FontUri.ToString(), fontWeight = font.Weight.ToOpenTypeWeight(),
            renderedFonts = Descendants<FrameworkElement>(Card).Where(element => element.IsVisible).SelectMany(element => GlyphFonts(VisualTreeHelper.GetDrawing(element))).Distinct().ToArray(),
            clipping = Descendants<RollingText>(Card).Any(text => text.IsVisible && text.DesiredSize.Width > text.ActualWidth + 1)
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    static IEnumerable<string> GlyphFonts(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
        if (drawing is DrawingGroup group)
            foreach (var child in group.Children)
                foreach (var font in GlyphFonts(child)) yield return font;
    }

    async Task CheckMotionAsync(string path)
    {
        var results = new Dictionary<string, bool>();
        await Task.Delay(800);
        var initialPercent = PercentText.Text;
        ChangePresentation("strip");
        await Task.Delay(120);
        results["transition visible"] = !MotionEnabled || TransitionGhost.IsVisible;
        results["size interpolates"] = !MotionEnabled || Math.Abs(ActualWidth - PresentationWidth) > 0.5;
        var framePath = Path.ChangeExtension(path, ".png");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(RenderVisual(this)));
        using (var file = File.Create(framePath)) encoder.Save(file);
        await Task.Delay(600);
        results["strip settles"] = Math.Abs(ActualWidth - 574) < 1 && TransitionGhost.Visibility == Visibility.Collapsed;
        foreach (var style in new[] { "clock", "orbit", "panel" })
        {
            ChangePresentation(style); await Task.Delay(600);
            results[style + " settles"] = Math.Abs(ActualWidth - PresentationWidth) < 1 && TransitionGhost.Visibility == Visibility.Collapsed && Card.Opacity > 0.99;
        }
        ChangePresentation("strip"); ChangePresentation("orbit"); ChangePresentation("clock");
        await Task.Delay(700);
        results["rapid switches settle"] = settings.Style == "clock" && Math.Abs(ActualWidth - 356) < 1 && TransitionGhost.Visibility == Visibility.Collapsed;
        results["quota preserved"] = PercentText.Text == initialPercent;
        settings.Motion = false; ApplyMotion(); ChangePresentation("strip");
        results["reduced motion instant"] = Math.Abs(ActualWidth - 574) < 1 && TransitionGhost.Visibility == Visibility.Collapsed;
        results["rolling glyphs stopped"] = Descendants<RollingText>(Card).All(text => !text.IsAnimating);
        File.WriteAllText(path, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        if (results.Values.Any(passed => !passed)) Environment.ExitCode = 1;
    }

    void LoadFixture(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.TryGetProperty("testError", out _)) { failed = true; ShowEmpty("暂时无法同步", "请检查网络与 Codex 登录状态，稍后会自动重试。"); return; }
        ApplySnapshot(QuotaSnapshot.Parse(doc.RootElement));
    }
    static QuotaSnapshot DemoSnapshot() => new(DateTimeOffset.UtcNow,
        [new("codex", "Codex", "plus", [new(28, 300, DateTimeOffset.UtcNow.AddHours(2).AddMinutes(17).AddSeconds(42)),
            new(11, 10080, DateTimeOffset.UtcNow.AddDays(4).AddHours(8).AddMinutes(35))], null, false)], true);
}
