param(
    [Parameter(Mandatory = $true)]
    [string]$DesktopExe
)

$ErrorActionPreference = "Stop"

$resolvedDesktop = (Resolve-Path -LiteralPath $DesktopExe).Path
$commandPrefix = '"{0}"' -f $resolvedDesktop
$stringKind = [Microsoft.Win32.RegistryValueKind]::String

$encryptKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey(
    'Software\Classes\*\shell\CompanyDlp.Encrypt'
)

try {
    $encryptKey.SetValue('MUIVerb', 'Encrypt with Company DLP', $stringKind)
    $encryptKey.SetValue('Icon', $resolvedDesktop, $stringKind)
    $encryptKey.SetValue('Position', 'Top', $stringKind)
    $encryptKey.SetValue('AppliesTo', 'System.FileExtension:<>".dlpenc"', $stringKind)

    $encryptCommandKey = $encryptKey.CreateSubKey('command')
    try {
        $encryptCommandKey.SetValue(
            '',
            $commandPrefix + ' --encrypt-and-delete "%1"',
            $stringKind
        )
    }
    finally {
        $encryptCommandKey.Dispose()
    }
}
finally {
    $encryptKey.Dispose()
}

$decryptKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey(
    'Software\Classes\SystemFileAssociations\.dlpenc\shell\CompanyDlp.Decrypt'
)

try {
    $decryptKey.SetValue('MUIVerb', 'Decrypt with Company DLP', $stringKind)
    $decryptKey.SetValue('Icon', $resolvedDesktop, $stringKind)
    $decryptKey.SetValue('Position', 'Top', $stringKind)

    $decryptCommandKey = $decryptKey.CreateSubKey('command')
    try {
        $decryptCommandKey.SetValue(
            '',
            $commandPrefix + ' --decrypt "%1"',
            $stringKind
        )
    }
    finally {
        $decryptCommandKey.Dispose()
    }
}
finally {
    $decryptKey.Dispose()
}

Write-Host "Development File Explorer context-menu actions registered through CompanyDlp.Desktop.exe." -ForegroundColor Green
Write-Host "On Windows 11, use Right click -> Show more options." -ForegroundColor DarkGray
