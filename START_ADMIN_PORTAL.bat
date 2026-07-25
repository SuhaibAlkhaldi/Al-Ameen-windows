@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-admin-portal.ps1" %*
exit /b %ERRORLEVEL%
