param(
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$payloadRoot = Join-Path $PSScriptRoot "payload"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $root ".development\browser-download-v2-backup-$timestamp"

$copyTargets = @(
    @{ Source = Join-Path $payloadRoot "browser-extension\service-worker.js"; Target = Join-Path $root "browser-extension\service-worker.js" },
    @{ Source = Join-Path $payloadRoot "browser-extension\icon128.png"; Target = Join-Path $root "browser-extension\icon128.png" },
    @{ Source = Join-Path $payloadRoot "firefox-extension\background-firefox.js"; Target = Join-Path $root "firefox-extension\background-firefox.js" },
    @{ Source = Join-Path $payloadRoot "firefox-extension\icon128.png"; Target = Join-Path $root "firefox-extension\icon128.png" },
    @{ Source = Join-Path $payloadRoot "scripts\set-development-permission.ps1"; Target = Join-Path $root "scripts\set-development-permission.ps1" }
)

$developmentPolicyPath = Join-Path $root "config\policy.development.json"
$productionPolicyPath = Join-Path $root "config\policy.production.sample.json"
$chromeManifestPath = Join-Path $root "browser-extension\manifest.json"
$firefoxManifestPath = Join-Path $root "firefox-extension\manifest.json"
$rootPermissionScript = Join-Path $root "set-development-permission.fixed.ps1"

foreach ($item in $copyTargets) {
    if (-not (Test-Path -LiteralPath $item.Source)) {
        throw "Hotfix payload file was not found: $($item.Source)"
    }
}
foreach ($required in @(
    $developmentPolicyPath,
    $productionPolicyPath,
    $chromeManifestPath,
    $firefoxManifestPath,
    (Join-Path $root "browser-extension\service-worker.js"),
    (Join-Path $root "firefox-extension\background-firefox.js")
)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required project file was not found: $required"
    }
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

function Backup-ProjectFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $relative = $Path.Substring($root.Length).TrimStart('\')
    $backupPath = Join-Path $backupRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
    Copy-Item -LiteralPath $Path -Destination $backupPath -Force
}

function Write-JsonUtf8NoBom {
    param([string]$Path, [object]$Value)
    $json = $Value | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText(
        $Path,
        $json,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Ensure-Permission {
    param([object]$Manifest, [string]$Permission)
    $items = @($Manifest.permissions)
    if ($items -notcontains $Permission) {
        $Manifest.permissions = @($items + $Permission)
    }
}

function Configure-Policy {
    param([string]$Path)
    Backup-ProjectFile -Path $Path
    $policy = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $policy.browser.blockDownloads = $false

    $property = $policy.permissions.defaultPermissions.PSObject.Properties["browser.download"]
    if ($null -eq $property) {
        $policy.permissions.defaultPermissions |
            Add-Member -MemberType NoteProperty -Name "browser.download" -Value $false
    }
    else {
        $property.Value = $false
    }

    Write-JsonUtf8NoBom -Path $Path -Value $policy
}

Write-Host "[1/6] Backing up current browser files." -ForegroundColor Cyan
foreach ($item in $copyTargets) { Backup-ProjectFile -Path $item.Target }
Backup-ProjectFile -Path $rootPermissionScript
Backup-ProjectFile -Path $chromeManifestPath
Backup-ProjectFile -Path $firefoxManifestPath

Write-Host "[2/6] Installing race-safe download interception." -ForegroundColor Cyan
foreach ($item in $copyTargets) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $item.Target) -Force | Out-Null
    Copy-Item -LiteralPath $item.Source -Destination $item.Target -Force
}
Copy-Item -LiteralPath (Join-Path $payloadRoot "scripts\set-development-permission.ps1") `
    -Destination $rootPermissionScript -Force

Write-Host "[3/6] Configuring browser.download as Default Deny." -ForegroundColor Cyan
Configure-Policy -Path $developmentPolicyPath
Configure-Policy -Path $productionPolicyPath

Write-Host "[4/6] Updating extension manifests to v3.0.2." -ForegroundColor Cyan
$chromeManifest = Get-Content -LiteralPath $chromeManifestPath -Raw | ConvertFrom-Json
$chromeManifest.version = "3.0.2"
Ensure-Permission -Manifest $chromeManifest -Permission "notifications"
$chromeManifest | Add-Member -MemberType NoteProperty -Name "icons" -Value ([PSCustomObject]@{"128" = "icon128.png"}) -Force
Write-JsonUtf8NoBom -Path $chromeManifestPath -Value $chromeManifest

$firefoxManifest = Get-Content -LiteralPath $firefoxManifestPath -Raw | ConvertFrom-Json
$firefoxManifest.version = "3.0.2"
Ensure-Permission -Manifest $firefoxManifest -Permission "notifications"
$firefoxManifest | Add-Member -MemberType NoteProperty -Name "icons" -Value ([PSCustomObject]@{"128" = "icon128.png"}) -Force
Write-JsonUtf8NoBom -Path $firefoxManifestPath -Value $firefoxManifest

Write-Host "[5/6] Removing user-level DownloadRestrictions." -ForegroundColor Cyan
foreach ($registryPath in @(
    "HKCU:\SOFTWARE\Policies\Google\Chrome",
    "HKCU:\SOFTWARE\Policies\Microsoft\Edge"
)) {
    if (Test-Path -LiteralPath $registryPath) {
        Remove-ItemProperty -LiteralPath $registryPath `
            -Name "DownloadRestrictions" -Force -ErrorAction SilentlyContinue
    }
}

$machinePolicyFound = $false
foreach ($registryPath in @(
    "HKLM:\SOFTWARE\Policies\Google\Chrome",
    "HKLM:\SOFTWARE\Policies\Microsoft\Edge"
)) {
    $value = Get-ItemProperty -LiteralPath $registryPath `
        -Name "DownloadRestrictions" -ErrorAction SilentlyContinue
    if ($null -ne $value -and $null -ne $value.DownloadRestrictions) {
        $machinePolicyFound = $true
        Write-Warning "$registryPath has machine-level DownloadRestrictions=$($value.DownloadRestrictions)."
    }
}

Write-Host "[6/6] Verifying installed files." -ForegroundColor Cyan
$worker = Get-Content -LiteralPath (Join-Path $root "browser-extension\service-worker.js") -Raw
$policy = Get-Content -LiteralPath $developmentPolicyPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $chromeManifestPath -Raw | ConvertFrom-Json

if ($worker -notmatch 'Pause immediately before calling the Service') {
    throw "The race-safe Chrome download listener was not installed."
}
if ($worker -notmatch 'removeFile') {
    throw "Completed-download cleanup was not installed."
}
if ($policy.browser.blockDownloads -ne $false) {
    throw "Legacy DownloadRestrictions mode is still enabled."
}
if ($policy.permissions.defaultPermissions.PSObject.Properties["browser.download"].Value -ne $false) {
    throw "browser.download is not Default Deny."
}
if ($manifest.version -ne "3.0.2") {
    throw "Chrome extension version was not updated."
}
if (@($manifest.permissions) -notcontains "notifications") {
    throw "Chrome notifications permission was not added."
}

Write-Host ""
Write-Host "Browser download v2 hotfix applied successfully." -ForegroundColor Green
Write-Host "No C# source, DLL, EXE, solution, launcher, or Windows Service file was modified." -ForegroundColor Green
Write-Host "No dotnet build is required." -ForegroundColor Green
Write-Host "Backup directory: $backupRoot" -ForegroundColor DarkGray
Write-Host ""
Write-Host "Close only the protected Development browser window, then use Start Test Session again." -ForegroundColor Cyan
Write-Host "Confirm extension version 3.0.2 at chrome://extensions or edge://extensions." -ForegroundColor Cyan

if ($machinePolicyFound) {
    Write-Warning "A machine-level DownloadRestrictions policy remains. It can prevent Temporary Allow."
}
