@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Set-Version.ps1" %*

if "%~1"=="" (
    pause
)
