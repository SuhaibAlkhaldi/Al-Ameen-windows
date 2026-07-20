$ErrorActionPreference = "SilentlyContinue"
Remove-Item "HKCU:\Software\Classes\*\shell\CompanyDlp.Encrypt" -Recurse -Force
Remove-Item "HKCU:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt" -Recurse -Force
Write-Host "Development File Explorer context-menu actions removed." -ForegroundColor DarkGray
