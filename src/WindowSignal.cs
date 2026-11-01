using System;
using System.Runtime.InteropServices;

namespace CodexQuotaWidget;

internal static class WindowSignal
{
    internal static readonly int ShowMessage = RegisterWindowMessage("CodexQuotaWidget.Show.v1");
    internal static void ShowExisting()
    {
        // WPF hides taskbar-less windows behind an owner; HWND_BROADCAST can skip them.
        var window = FindWindow(null, "Codex 额度");
        PostMessage(window != IntPtr.Zero ? window : new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
