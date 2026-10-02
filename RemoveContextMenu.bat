@echo off
setlocal
cd /d "%~dp0"

echo Removing Quick Editor from Windows Right-Click Context Menu...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-ExplorerMenu.ps1" -Unregister

echo.
pause
