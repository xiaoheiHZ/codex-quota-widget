using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace CodexQuotaWidget;

public partial class App : System.Windows.Application
{
    Mutex? instanceMutex;
    internal static bool Demo { get; private set; }
    internal static string? CapturePath { get; private set; }
    internal static string? Fixture { get; private set; }
    internal static string? InitialStyle { get; private set; }
    internal static bool NoMotion { get; private set; }
    internal static string? MotionCheckPath { get; private set; }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Demo = e.Args.Contains("--demo");
        CapturePath = ValueAfter(e.Args, "--capture");
        Fixture = ValueAfter(e.Args, "--fixture");
        InitialStyle = RawAfter(e.Args, "--style");
        NoMotion = e.Args.Contains("--no-motion");
        MotionCheckPath = ValueAfter(e.Args, "--check-motion");
        if (e.Args.Contains("--diagnose"))
        {
            var path = ValueAfter(e.Args, "--diagnose") ?? Path.Combine(AppContext.BaseDirectory, "quota-diagnostic.json");
            try
            {
                await using var client = new CodexClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                await client.StartAsync(timeout.Token);
                var quota = await client.ReadQuotaAsync(timeout.Token);
                File.WriteAllText(path, JsonSerializer.Serialize(new { ok = true, quota }, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { File.WriteAllText(path, JsonSerializer.Serialize(new { ok = false, error = ex.GetType().Name })); Environment.ExitCode = 1; }
            Shutdown(); return;
        }
        if (!Demo && CapturePath == null && Fixture == null && MotionCheckPath == null)
        {
            instanceMutex = new Mutex(true, @"Local\CodexQuotaWidget", out var first);
            if (!first) { WindowSignal.ShowExisting(); Shutdown(); return; }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            // Never persist raw upstream bodies or token-bearing error messages.
            System.Windows.MessageBox.Show("组件遇到了问题，请重新打开。\n错误类型：" + args.Exception.GetType().Name, "Codex 额度", MessageBoxButton.OK, MessageBoxImage.Information);
            args.Handled = true;
            Shutdown(1);
        };
        var window = new MainWindow(); MainWindow = window; window.Show();
    }
    static string? ValueAfter(string[] args, string flag)
        => RawAfter(args, flag) is { } value ? Path.GetFullPath(value) : null;
    static string? RawAfter(string[] args, string flag)
    {
        var n = Array.IndexOf(args, flag);
        return n >= 0 && n + 1 < args.Length && !args[n + 1].StartsWith("--") ? args[n + 1] : null;
    }
    protected override void OnExit(ExitEventArgs e) { instanceMutex?.Dispose(); base.OnExit(e); }
}
