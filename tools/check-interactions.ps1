param([int]$WidgetProcessId)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class WidgetNative {
 delegate bool Callback(IntPtr window, IntPtr parameter);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback callback, IntPtr parameter);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
 [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
 public static IntPtr[] Windows(uint target) { var result=new List<IntPtr>(); EnumWindows((window,_) => { GetWindowThreadProcessId(window,out var id); if(id==target) result.Add(window); return true; },IntPtr.Zero); return result.ToArray(); }
 public static IntPtr FindMain(uint target) { foreach(var window in Windows(target)) { var name=new StringBuilder(256); GetWindowText(window,name,256); if(name.ToString()=="Codex 额度") return window; } throw new Exception("Widget window missing"); }
}
'@
$targetWindow = [WidgetNative]::FindMain($WidgetProcessId)
$targetRoot = [System.Windows.Automation.AutomationElement]::FromHandle($targetWindow)
function Find-WidgetElement([string]$Name) {
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    foreach ($handle in [WidgetNative]::Windows($WidgetProcessId)) {
        if (-not [WidgetNative]::IsWindowVisible($handle)) { continue }
        $elementRoot = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
        $element = $elementRoot.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
    }
    throw "Widget control missing: $Name"
}
function Invoke-WidgetElement([string]$Name) {
    $element = Find-WidgetElement $Name
    $pattern = $null
    if ($element.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
    elseif ($element.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$pattern)) { $pattern.Expand() }
    else { throw "No action pattern: $Name" }
    Start-Sleep -Milliseconds 650
}
function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; Write-Output "PASS $Message" }
$normalHeight = $targetRoot.Current.BoundingRectangle.Height
$normalWidth = $targetRoot.Current.BoundingRectangle.Width
foreach ($expectedWidth in @(574,356,356,356)) {
    Invoke-WidgetElement '切换展示样式'
    Require ([Math]::Abs($targetRoot.Current.BoundingRectangle.Width - $expectedWidth) -lt 2) "style switch settles at width $expectedWidth"
}
Require ([Math]::Abs($targetRoot.Current.BoundingRectangle.Height - $normalHeight) -lt 2) 'four styles cycle back to the original layout'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '展示样式'
Invoke-WidgetElement '数字时钟'
Require ($targetRoot.Current.BoundingRectangle.Height -gt $normalHeight) 'style submenu selects the clock layout'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '展示样式'
Invoke-WidgetElement '经典面板'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '动效强度'
Invoke-WidgetElement '轻柔'
$settingsPath = Join-Path $env:LOCALAPPDATA 'CodexQuotaWidget\settings.json'
Require ((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).MotionLevel -eq 'soft') 'soft motion preference saved'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '动效强度'
Invoke-WidgetElement '流畅'
Require ((Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json).MotionLevel -eq 'fluid') 'fluid motion preference restored'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '精简模式'
$compactHeight = $targetRoot.Current.BoundingRectangle.Height
Require ($compactHeight -lt $normalHeight) 'compact mode reduces window height'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '精简模式'
Require ([Math]::Abs($targetRoot.Current.BoundingRectangle.Height - $normalHeight) -lt 2) 'normal mode restored'
$pinBefore = [WidgetNative]::GetWindowLong($targetWindow, -20) -band 8
Invoke-WidgetElement '切换置顶'
Require (([WidgetNative]::GetWindowLong($targetWindow, -20) -band 8) -ne $pinBefore) 'pin toggles native topmost style'
Invoke-WidgetElement '切换置顶'
Require (([WidgetNative]::GetWindowLong($targetWindow, -20) -band 8) -eq $pinBefore) 'pin restored'
Invoke-WidgetElement '组件设置'
Invoke-WidgetElement '隐藏到托盘'
Require (-not [WidgetNative]::IsWindowVisible($targetWindow)) 'hide to tray'
$appPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\release\app\CodexQuotaWidget.exe'))
$secondInstance = Start-Process -FilePath $appPath -WindowStyle Hidden -PassThru
if (-not $secondInstance.WaitForExit(5000)) { throw 'Second instance did not exit' }
Start-Sleep -Milliseconds 300
Require ([WidgetNative]::IsWindowVisible($targetWindow)) 'second launch restores tray window'
Invoke-WidgetElement '刷新额度'
Require ($targetRoot.Current.IsEnabled) 'refresh action keeps UI responsive'
Write-Output '15 interaction checks passed.'
