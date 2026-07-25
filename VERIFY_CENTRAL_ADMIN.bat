@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify-central-admin.ps1"
set EXIT_CODE=%ERRORLEVEL%
if not "%EXIT_CODE%"=="0" (
  echo.
  echo Central Admin verification failed with exit code %EXIT_CODE%.
)
endlocal & exit /b %EXIT_CODE%
