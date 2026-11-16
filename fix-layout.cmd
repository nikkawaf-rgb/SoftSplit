@echo off
rem Emergency: restore the normal "extend" layout if displays end up in a weird state.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0MirrorDisplay.ps1" -Action off
timeout /t 2 >nul
