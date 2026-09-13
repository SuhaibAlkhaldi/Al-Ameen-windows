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

# .dlpenc ProgID + default "open" command - lets a double-clicked .dlpenc route through
# FileProtectionCoordinator.ExecuteOpenAccessAsync (via ShellCryptoCommandRunner's request-access
# verb) instead of Explorer's "how do you want to open this file?" picker. Distinct from the
# CompanyDlp.Decrypt verb above (still registered, for an explicit right-click decrypt) - this is
# what makes the extension a real, recognized file type at all; before this there was no ProgID for
# .dlpenc anywhere, so double-click did nothing special. A machine/user that already manually chose
# an app for .dlpenc before this shipped may have a HKCU FileExts\.dlpenc\UserChoice override that
# takes precedence over this - not handled here, an accepted edge case (see the plan's known-limits).
$progIdName = 'CompanyDlp.EncryptedFile'

$extensionKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Classes\.dlpenc')
try {
    $extensionKey.SetValue('', $progIdName, $stringKind)
}
finally {
    $extensionKey.Dispose()
}

$progIdKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("Software\Classes\$progIdName")
try {
    $progIdKey.SetValue('', 'Company DLP Encrypted File', $stringKind)

    $progIdIconKey = $progIdKey.CreateSubKey('DefaultIcon')
    try {
        $progIdIconKey.SetValue('', $resolvedDesktop, $stringKind)
    }
    finally {
        $progIdIconKey.Dispose()
    }

    $openCommandKey = $progIdKey.CreateSubKey('shell\open\command')
    try {
        $openCommandKey.SetValue(
            '',
            $commandPrefix + ' --request-access "%1"',
            $stringKind
        )
    }
    finally {
        $openCommandKey.Dispose()
    }
}
finally {
    $progIdKey.Dispose()
}

Write-Host "Development File Explorer context-menu actions registered through CompanyDlp.Desktop.exe." -ForegroundColor Green
Write-Host "On Windows 11, use Right click -> Show more options." -ForegroundColor DarkGray
Write-Host "Double-clicking a .dlpenc file now routes through file.open-access instead of doing nothing." -ForegroundColor DarkGray
