#Requires -RunAsAdministrator
$ErrorActionPreference = "SilentlyContinue"
# -LiteralPath: the "*" is the literal registry key name (same meaning as HKEY_CLASSES_ROOT\*, "all
# file types"), not a wildcard - without -LiteralPath, PowerShell glob-expands it against every
# subkey under Classes (tens of thousands on a real machine), which is both wrong and extremely slow.
Remove-Item -LiteralPath "HKLM:\Software\Classes\*\shell\CompanyDlp.Encrypt" -Recurse -Force
Remove-Item -LiteralPath "HKLM:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt" -Recurse -Force
Write-Host "Production File Explorer context-menu actions removed." -ForegroundColor DarkGray
