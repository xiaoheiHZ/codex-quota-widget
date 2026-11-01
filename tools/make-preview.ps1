$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase
$projectDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$previewDirectory = Join-Path $projectDirectory 'release\previews'
$appPath = Join-Path $projectDirectory 'release\app\CodexQuotaWidget.exe'
foreach ($styleName in @('panel','strip','clock','orbit')) {
    $capturePath = '"' + (Join-Path $previewDirectory ($styleName + '.png')) + '"'
    $captureProcess = Start-Process -FilePath $appPath -ArgumentList @('--demo','--style',$styleName,'--capture',$capturePath) -WindowStyle Hidden -PassThru -Wait
    if ($captureProcess.ExitCode -ne 0) { throw "Preview failed: $styleName" }
}
$fontDirectory = [uri]((Join-Path $projectDirectory 'src\Assets\Fonts') + '\')
$fontFamily = [System.Windows.Media.FontFamily]::new($fontDirectory, './#Source Han Sans CN')
$typeface = [System.Windows.Media.Typeface]::new($fontFamily, [System.Windows.FontStyles]::Normal, [System.Windows.FontWeights]::Medium, [System.Windows.FontStretches]::Normal)
$visual = [System.Windows.Media.DrawingVisual]::new()
$drawing = $visual.RenderOpen()
$background = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#F7F7F7'))
$ink = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#383838'))
$muted = [System.Windows.Media.SolidColorBrush]::new([System.Windows.Media.ColorConverter]::ConvertFromString('#8B8B8B'))
$drawing.DrawRectangle($background, $null, [System.Windows.Rect]::new(0,0,1164,800))
function Draw-Label([string]$Text,[double]$X,[double]$Y,[double]$Size,$Brush) {
    $formatted = [System.Windows.Media.FormattedText]::new($Text, [System.Globalization.CultureInfo]::GetCultureInfo('zh-CN'), [System.Windows.FlowDirection]::LeftToRight, $typeface, $Size, $Brush, [double]1)
    $drawing.DrawText($formatted, [System.Windows.Point]::new($X,$Y))
}
Draw-Label 'Codex · 展示样式' 36 16 20 $ink
Draw-Label 'Source Han Sans CN Medium' 873 23 12 $muted
$placements = @(
    @{Id='panel';Label='经典面板';X=24;Y=80},
    @{Id='clock';Label='数字时钟';X=404;Y=80},
    @{Id='orbit';Label='迷你仪表';X=784;Y=80},
    @{Id='strip';Label='横向状态条';X=295;Y=548}
)
foreach ($placement in $placements) {
    Draw-Label $placement.Label ($placement.X+12) ($placement.Y-20) 13 $muted
    $bitmap = [System.Windows.Media.Imaging.BitmapImage]::new([uri](Join-Path $previewDirectory ($placement.Id + '.png')))
    $drawing.DrawImage($bitmap, [System.Windows.Rect]::new($placement.X,$placement.Y,$bitmap.Width,$bitmap.Height))
}
$drawing.Close()
$rendered = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(1164,800,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
$rendered.Render($visual)
$encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($rendered))
$outputPath = Join-Path $previewDirectory 'styles-overview.png'
$outputFile = [System.IO.File]::Create($outputPath)
try { $encoder.Save($outputFile) } finally { $outputFile.Dispose() }
Write-Output $outputPath
