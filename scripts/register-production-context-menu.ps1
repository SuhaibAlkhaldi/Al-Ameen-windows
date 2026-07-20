#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)]
    [string]$DesktopExe
)

$ErrorActionPreference = "Stop"
$resolvedDesktop = (Resolve-Path $DesktopExe).Path
$commandPrefix = ('"{0}"' -f $resolvedDesktop)

$encryptKey = "HKLM:\Software\Classes\*\shell\CompanyDlp.Encrypt"
$encryptCommandKey = Join-Path $encryptKey "command"
New-Item $encryptCommandKey -Force | Out-Null
New-ItemProperty $encryptKey -Name "MUIVerb" -Value "Encrypt with Company DLP" -PropertyType String -Force | Out-Null
New-ItemProperty $encryptKey -Name "Icon" -Value $resolvedDesktop -PropertyType String -Force | Out-Null
New-ItemProperty $encryptKey -Name "Position" -Value "Top" -PropertyType String -Force | Out-Null
New-ItemProperty $encryptKey -Name "AppliesTo" -Value 'System.FileExtension:<>".dlpenc"' -PropertyType String -Force | Out-Null
Set-Item $encryptCommandKey -Value ($commandPrefix + ' --encrypt-and-delete "%1"')

$decryptKey = "HKLM:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt"
$decryptCommandKey = Join-Path $decryptKey "command"
New-Item $decryptCommandKey -Force | Out-Null
New-ItemProperty $decryptKey -Name "MUIVerb" -Value "Decrypt with Company DLP" -PropertyType String -Force | Out-Null
New-ItemProperty $decryptKey -Name "Icon" -Value $resolvedDesktop -PropertyType String -Force | Out-Null
New-ItemProperty $decryptKey -Name "Position" -Value "Top" -PropertyType String -Force | Out-Null
Set-Item $decryptCommandKey -Value ($commandPrefix + ' --decrypt "%1"')

Write-Host "Production File Explorer context-menu actions registered for all users." -ForegroundColor Green
