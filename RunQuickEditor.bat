@echo off
setlocal
cd /d "%~dp0"

REM Terminate any lingering QuickEditor instance so files aren't locked during build
taskkill /f /im QuickEditor.exe >nul 2>&1

dotnet build -p:Platform=x64 --nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

if "%~1"=="" (
    dotnet run --project QuickEditor.csproj -p:Platform=x64 --no-build
) else (
    dotnet run --project QuickEditor.csproj -p:Platform=x64 --no-build -- %*
)