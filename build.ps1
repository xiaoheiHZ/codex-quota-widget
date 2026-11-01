$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & ./tools/make-icon.ps1
    dotnet run --project tests/QuotaTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Quota checks failed.' }
    dotnet publish src/CodexQuotaWidget.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -o release/app --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    python tools/check-ui.py
    if ($LASTEXITCODE -ne 0) { throw 'UI checks failed.' }
    Copy-Item -LiteralPath './README.md' -Destination './release/app/使用说明.md' -Force
    Compress-Archive -LiteralPath './release/app/CodexQuotaWidget.exe','./release/app/使用说明.md','./release/app/SourceHanSans-LICENSE.txt' -DestinationPath './release/Codex额度小组件.zip' -Force
    Get-FileHash -LiteralPath './release/app/CodexQuotaWidget.exe' -Algorithm SHA256
} finally { Pop-Location }
