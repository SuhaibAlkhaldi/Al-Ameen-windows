@echo off
:: Double-click this to remove the Company DLP agent from this machine.
:: Automatically requests Administrator (UAC prompt) - just click Yes.
:: Audit and policy files under %ProgramData%\CompanyDlp are kept for investigation -
:: delete them manually only after approval.
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0Scripts\uninstall-production.ps1\"' -Verb RunAs -Wait"
echo.
pause
