@echo off
setlocal

set "PROJ=%~dp0Inkbridge.Windows.csproj"
set "EXE=%~dp0publish\Inkbridge.Windows.exe"

:: If --run-only flag is passed, skip build
if "%1"=="--run-only" goto :launch

echo [Inkbridge] Building...
dotnet publish -c Release -r win-x64 --self-contained false -o "%~dp0publish" "%PROJ%" > "%~dp0publish\build.log" 2>&1
if errorlevel 1 (
    echo [ERROR] Build failed. Check publish\build.log for details.
    pause
    exit /b 1
)
echo [Inkbridge] Build complete.

:launch
if not exist "%EXE%" (
    echo [ERROR] Executable not found. Run without --run-only to build first.
    pause
    exit /b 1
)

echo [Inkbridge] Starting service...
start "" "%EXE%"
echo [Inkbridge] Running. Look for the tray icon in the taskbar.
