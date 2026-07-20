#Requires -RunAsAdministrator
$ErrorActionPreference = "SilentlyContinue"
Remove-Item "HKLM:\Software\Classes\*\shell\CompanyDlp.Encrypt" -Recurse -Force
Remove-Item "HKLM:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt" -Recurse -Force
Write-Host "Production File Explorer context-menu actions removed." -ForegroundColor DarkGray
