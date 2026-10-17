@echo off
rem Starts Rhino or Revit with the local build of Caddis, see Start-Dev.ps1. Without arguments it asks which app.
rem e.g. Start-Dev.cmd Rhino8, Start-Dev.cmd Revit2026 -Configuration Release
where pwsh >nul 2>nul
if %errorlevel% equ 0 (set "shell=pwsh") else (set "shell=powershell")
%shell% -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-Dev.ps1" %*
if errorlevel 1 pause
