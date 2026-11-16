@echo off
rem Start mirroring the LG desktop onto the TV.
start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0MirrorWindow.ps1" -Fps 30
