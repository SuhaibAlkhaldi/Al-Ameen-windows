@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\connect-development-agent-to-admin.ps1" %*
exit /b %ERRORLEVEL%
