@echo off
setlocal
cd /d "%~dp0"

echo Registering Quick Editor in Windows Right-Click Context Menu...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Register-ExplorerMenu.ps1"

echo.
pause
