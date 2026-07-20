@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\scripts\validate-windows-ready.ps1"
if errorlevel 1 (
  echo.
  echo WINDOWS READY VALIDATION FAILED.
  exit /b 1
)
echo.
echo WINDOWS READY VALIDATION PASSED.
pause
