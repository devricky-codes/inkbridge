param (
    [switch]$RunAfterBuild = $false
)

$ErrorActionPreference = "Stop"

Write-Host "Publishing Inkbridge Windows App..." -ForegroundColor Cyan

# Publish as a single file Windows executable
dotnet publish f:\Projects\TabletEasyWrite\Inkbridge.Windows\Inkbridge.Windows.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Publish successful! Executable is located at:" -ForegroundColor Green
Write-Host "f:\Projects\TabletEasyWrite\Inkbridge.Windows\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Inkbridge.Windows.exe" -ForegroundColor Yellow

if ($RunAfterBuild) {
    Start-Process -FilePath "f:\Projects\TabletEasyWrite\Inkbridge.Windows\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\Inkbridge.Windows.exe"
}
