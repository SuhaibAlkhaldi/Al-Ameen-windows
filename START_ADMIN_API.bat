@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-admin-api.ps1"
exit /b %ERRORLEVEL%
