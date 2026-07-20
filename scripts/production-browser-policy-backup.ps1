#Requires -RunAsAdministrator
param([ValidateSet("Backup", "Restore")] [string]$Mode)
$ErrorActionPreference = "Stop"
$backupPath = Join-Path $env:ProgramData "CompanyDlp\browser-policy-backup.json"
$items = @(
    @{ Path = "SOFTWARE\Policies\Microsoft\Edge"; Name = "InPrivateModeAvailability" },
    @{ Path = "SOFTWARE\Policies\Microsoft\Edge"; Name = "BrowserGuestModeEnabled" },
    @{ Path = "SOFTWARE\Policies\Microsoft\Edge"; Name = "DisableScreenshots" },
    @{ Path = "SOFTWARE\Policies\Microsoft\Edge"; Name = "WebCaptureEnabled" },
    @{ Path = "SOFTWARE\Policies\Microsoft\Edge"; Name = "DownloadRestrictions" },
    @{ Path = "SOFTWARE\Policies\Google\Chrome"; Name = "IncognitoModeAvailability" },
    @{ Path = "SOFTWARE\Policies\Google\Chrome"; Name = "BrowserGuestModeEnabled" },
    @{ Path = "SOFTWARE\Policies\Google\Chrome"; Name = "DownloadRestrictions" }
)

if ($Mode -eq "Backup") {
    if (Test-Path $backupPath) { return }
    $snapshot = foreach ($item in $items) {
        $path = "HKLM:\$($item.Path)"
        $value = $null
        $exists = $false
        if (Test-Path $path) {
            try { $value = (Get-ItemProperty $path -Name $item.Name -ErrorAction Stop).$($item.Name); $exists = $true } catch { }
        }
        [pscustomobject]@{ path = $item.Path; name = $item.Name; existed = $exists; dwordValue = $value }
    }
    New-Item (Split-Path $backupPath -Parent) -ItemType Directory -Force | Out-Null
    $snapshot | ConvertTo-Json -Depth 4 | Set-Content $backupPath -Encoding UTF8
    return
}

if (Test-Path $backupPath) {
    $snapshot = Get-Content $backupPath -Raw | ConvertFrom-Json
    foreach ($item in $snapshot) {
        $path = "HKLM:\$($item.path)"
        New-Item $path -Force | Out-Null
        if ($item.existed) {
            New-ItemProperty $path -Name $item.name -Value ([int]$item.dwordValue) -PropertyType DWord -Force | Out-Null
        } else {
            Remove-ItemProperty $path -Name $item.name -Force -ErrorAction SilentlyContinue
        }
    }
    Remove-Item $backupPath -Force
}
