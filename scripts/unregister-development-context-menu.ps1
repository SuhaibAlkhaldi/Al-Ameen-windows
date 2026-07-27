$ErrorActionPreference = "SilentlyContinue"
# Remove-Item on a wildcard "Classes\*\..." path reliably hangs (not just slows down) - confirmed
# repeatedly with the equivalent HKLM path while building the shell extension. reg.exe handles the
# same wildcard path instantly, so use it here instead of PowerShell's registry cmdlets.
reg delete "HKCU\Software\Classes\*\shell\CompanyDlp.Encrypt" /f 2>$null
Remove-Item "HKCU:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt" -Recurse -Force
Write-Host "Development File Explorer context-menu actions removed." -ForegroundColor DarkGray
