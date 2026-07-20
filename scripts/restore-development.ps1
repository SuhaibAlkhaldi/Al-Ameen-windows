$ErrorActionPreference = "SilentlyContinue"
$unregisterContextMenu = Join-Path $PSScriptRoot "unregister-development-context-menu.ps1"
if (Test-Path $unregisterContextMenu) {
    & $unregisterContextMenu
}

$sessionFile = Join-Path $env:LOCALAPPDATA "CompanyDlp\Development\active-test-session.json"

if (Test-Path $sessionFile) {
    $snapshots = Get-Content $sessionFile -Raw | ConvertFrom-Json
    foreach ($snapshot in $snapshots) {
        $path = "HKCU:\$($snapshot.path)"
        New-Item $path -Force | Out-Null
        if ($snapshot.existed) {
            New-ItemProperty $path -Name $snapshot.name -Value ([int]$snapshot.dwordValue) -PropertyType DWord -Force | Out-Null
        } else {
            Remove-ItemProperty $path -Name $snapshot.name -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $sessionFile -Force
}

$nativeBackup = Join-Path $env:LOCALAPPDATA "CompanyDlp\Development\native-host-backup.json"
if (Test-Path $nativeBackup) {
    $nativeSnapshots = Get-Content $nativeBackup -Raw | ConvertFrom-Json
    foreach ($snapshot in $nativeSnapshots) {
        $path = "HKCU:\$($snapshot.path)"
        if ($snapshot.existed) {
            New-Item $path -Force | Out-Null
            Set-Item $path -Value $snapshot.value
        } else {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $nativeBackup -Force
} else {
    Remove-Item "HKCU:\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item "HKCU:\SOFTWARE\Mozilla\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue
}

Get-CimInstance Win32_Process | Where-Object {
    $_.Name -in @("msedge.exe", "chrome.exe") -and
    $_.CommandLine -like "*CompanyDlp*Development*BrowserProfiles*"
} | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

Remove-Item (Join-Path $env:LOCALAPPDATA "CompanyDlp\Development\BrowserProfiles") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Development browser policies, native host registration, File Explorer context-menu actions, and temporary browser profiles were restored/removed." -ForegroundColor Green
