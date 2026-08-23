#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)]
    [string]$DesktopExe
)

$ErrorActionPreference = "Stop"
$resolvedDesktop = (Resolve-Path $DesktopExe).Path
$commandPrefix = ('"{0}"' -f $resolvedDesktop)

# IMPORTANT: the "*" here is the literal, standard Windows registry key name that means "all file
# types" (same as under HKEY_CLASSES_ROOT\*) - it is NOT a wildcard. But PowerShell's registry
# provider *cmdlets* treat "*" in -Path as a glob by default, so a plain `New-Item $encryptCommandKey`
# here silently expands to "every subkey under HKLM:\Software\Classes\" (tens of thousands of
# ProgIDs/CLSIDs on a real machine) and appears to hang for minutes with no error and no progress -
# confirmed live as the real cause of the install hanging at this exact step. -LiteralPath fixes this
# for New-ItemProperty/Set-ItemProperty/Remove-Item, but New-Item and Set-Item don't have a
# -LiteralPath parameter at all ("A parameter cannot be found that matches parameter name
# 'LiteralPath'" - also confirmed live). So instead, use the raw .NET Microsoft.Win32.Registry API
# for this key, which takes a plain literal string and never does any wildcard expansion - the exact
# same technique register-development-context-menu.ps1 already uses for this identical key.
$encryptRegKey = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey('Software\Classes\*\shell\CompanyDlp.Encrypt')
try {
    $encryptRegKey.SetValue('MUIVerb', 'Encrypt with Company DLP', [Microsoft.Win32.RegistryValueKind]::String)
    $encryptRegKey.SetValue('Icon', $resolvedDesktop, [Microsoft.Win32.RegistryValueKind]::String)
    $encryptRegKey.SetValue('Position', 'Top', [Microsoft.Win32.RegistryValueKind]::String)
    $encryptRegKey.SetValue('AppliesTo', 'System.FileExtension:<>".dlpenc"', [Microsoft.Win32.RegistryValueKind]::String)
    $encryptCommandRegKey = $encryptRegKey.CreateSubKey('command')
    try {
        $encryptCommandRegKey.SetValue('', $commandPrefix + ' --encrypt-and-delete "%1"', [Microsoft.Win32.RegistryValueKind]::String)
    } finally {
        $encryptCommandRegKey.Dispose()
    }
} finally {
    $encryptRegKey.Dispose()
}

$decryptKey = "HKLM:\Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt"
$decryptCommandKey = Join-Path $decryptKey "command"
New-Item $decryptCommandKey -Force | Out-Null
New-ItemProperty $decryptKey -Name "MUIVerb" -Value "Decrypt with Company DLP" -PropertyType String -Force | Out-Null
New-ItemProperty $decryptKey -Name "Icon" -Value $resolvedDesktop -PropertyType String -Force | Out-Null
New-ItemProperty $decryptKey -Name "Position" -Value "Top" -PropertyType String -Force | Out-Null
Set-Item $decryptCommandKey -Value ($commandPrefix + ' --decrypt "%1"')

Write-Host "Production File Explorer context-menu actions registered for all users." -ForegroundColor Green
