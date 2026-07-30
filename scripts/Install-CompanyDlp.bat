@echo off
:: Double-click this to install the Company DLP agent on this machine.
:: Automatically requests Administrator (UAC prompt) - just click Yes.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0deploy-agent-portable.ps1\"' -Verb RunAs -Wait"
echo.
pause
