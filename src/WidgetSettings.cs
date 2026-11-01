using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexQuotaWidget;

public sealed class WidgetSettings
{
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Topmost { get; set; } = true;
    public bool Compact { get; set; }
    public bool Motion { get; set; } = true;
    public string Style { get; set; } = "panel";
    public string MotionLevel { get; set; } = "fluid";
    public string? SelectedBucket { get; set; }
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaWidget");
    static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static WidgetSettings Load()
    {
        try { return JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(SettingsPath)) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(SettingsPath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(SettingsPath + ".tmp", SettingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool AutoStart
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue("CodexQuotaWidget") is string; }
    }
    public static void SetAutoStart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue("CodexQuotaWidget", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("CodexQuotaWidget", false);
    }
}
