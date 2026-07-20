@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\run-development.ps1" -SkipShellIntegration
set "DLP_EXIT_CODE=%ERRORLEVEL%"
if not "%DLP_EXIT_CODE%"=="0" (
    echo.
    echo Company DLP Core Development startup failed with exit code %DLP_EXIT_CODE%.
    echo Review .development\logs\launcher.error.log and launcher.log.
)
pause
exit /b %DLP_EXIT_CODE%
