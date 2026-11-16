@echo off
rem Stop the mirror window (if running).
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$f = Join-Path $env:TEMP 'SoftSplitMirror.pid'; if (Test-Path $f) { $id = [int](Get-Content $f -Raw); Stop-Process -Id $id -Force -ErrorAction SilentlyContinue; Remove-Item $f -Force -ErrorAction SilentlyContinue; Write-Host 'Mirror stopped' } else { Write-Host 'Mirror is not running' }"
timeout /t 1 >nul
