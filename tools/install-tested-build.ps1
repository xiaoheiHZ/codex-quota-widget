$ErrorActionPreference = 'Stop'
$projectDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseDirectory = Join-Path $projectDirectory 'release'
$destination = Join-Path $releaseDirectory 'app\CodexQuotaWidget.exe'
$source = Join-Path $releaseDirectory 'next\CodexQuotaWidget.exe'
if (-not (Test-Path -LiteralPath $source)) { throw 'Tested build is missing.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WidgetUpdateWindow {
 delegate bool Callback(IntPtr window, IntPtr parameter);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback,IntPtr parameter);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint process);
 [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,int message,IntPtr wp,IntPtr lp);
 public static void Close(uint target) { EnumWindows((window,_) => { GetWindowThreadProcessId(window,out var id); if(id==target) PostMessage(window,0x10,IntPtr.Zero,IntPtr.Zero); return true; },IntPtr.Zero); }
}
'@
foreach ($running in @(Get-Process CodexQuotaWidget -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $destination })) {
    [WidgetUpdateWindow]::Close($running.Id)
    if (-not $running.WaitForExit(8000)) { throw 'Please close the running widget before updating.' }
}
$previousZip = Join-Path $releaseDirectory 'Codex额度小组件-1.0.0.zip'
$currentZip = Join-Path $releaseDirectory 'Codex额度小组件.zip'
if ((Test-Path -LiteralPath $currentZip) -and -not (Test-Path -LiteralPath $previousZip)) { Copy-Item -LiteralPath $currentZip -Destination $previousZip }
Copy-Item -LiteralPath $source -Destination $destination -Force
Copy-Item -LiteralPath (Join-Path $releaseDirectory 'next\SourceHanSans-LICENSE.txt') -Destination (Join-Path $releaseDirectory 'app\SourceHanSans-LICENSE.txt') -Force
Copy-Item -LiteralPath (Join-Path $projectDirectory 'README.md') -Destination (Join-Path $releaseDirectory 'app\使用说明.md') -Force
$startedWidget = Start-Process -FilePath $destination -PassThru
$startedWidget.Id | Set-Content -LiteralPath (Join-Path $releaseDirectory 'live-pid.txt')
Write-Output "Updated widget PID: $($startedWidget.Id)"
