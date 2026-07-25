@echo off
setlocal
cd /d "%~dp0"

where powershell.exe >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found.
  exit /b 1
)

start "Company DLP Admin API" powershell.exe -NoExit -ExecutionPolicy Bypass -File "%~dp0scripts\start-admin-api.ps1"
start "Company DLP Admin Portal" powershell.exe -NoExit -ExecutionPolicy Bypass -File "%~dp0scripts\start-admin-portal.ps1"

echo Company DLP Admin API and Angular Admin Portal were launched in separate windows.
echo API:    http://127.0.0.1:5060
echo Portal: http://127.0.0.1:4200
endlocal
