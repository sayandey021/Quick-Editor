@echo off
setlocal
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Packages.ps1" %*

if "%~1"=="" (
    pause
)
