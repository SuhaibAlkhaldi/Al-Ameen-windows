@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\set-development-permission.ps1"
if errorlevel 1 (
  echo.
  echo Failed to register the temporary permission.
)
pause
