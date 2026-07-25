param(
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$chromeWorker = Join-Path $root "browser-extension\service-worker.js"
$firefoxWorker = Join-Path $root "firefox-extension\background-firefox.js"
$chromeManifest = Join-Path $root "browser-extension\manifest.json"
$firefoxManifest = Join-Path $root "firefox-extension\manifest.json"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $root ".development\browser-download-alert-v3-backup-$timestamp"

foreach ($required in @($chromeWorker, $chromeManifest)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required file was not found: $required"
    }
}

New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null

function Backup-File {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $relative = $Path.Substring($root.Length).TrimStart('\')
    $target = Join-Path $backupRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $Path -Destination $target -Force
}

function Write-Utf8NoBom {
    param(
        [string]$Path,
        [string]$Content
    )

    [System.IO.File]::WriteAllText(
        $Path,
        $Content,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

function Write-JsonUtf8NoBom {
    param(
        [string]$Path,
        [object]$Value
    )

    Write-Utf8NoBom -Path $Path -Content ($Value | ConvertTo-Json -Depth 100)
}

Write-Host "[1/5] Backing up extension files." -ForegroundColor Cyan
foreach ($path in @($chromeWorker, $firefoxWorker, $chromeManifest, $firefoxManifest)) {
    Backup-File -Path $path
}

Write-Host "[2/5] Moving the blocked-download alert before Audit delivery." -ForegroundColor Cyan

$chromeText = [System.IO.File]::ReadAllText($chromeWorker)
$chromeOld = @'
  await stopAndRemoveDownload(downloadItem.id);
  await auditDownload(downloadItem, "blocked", decision);
  showDownloadBlockedAlert(downloadItem);
'@
$chromeNew = @'
  await stopAndRemoveDownload(downloadItem.id);

  // Alert immediately. Audit delivery must never delay user feedback.
  showDownloadBlockedAlert(downloadItem);
  void auditDownload(downloadItem, "blocked", decision);
'@

if ($chromeText.Contains($chromeOld)) {
    $chromeText = $chromeText.Replace($chromeOld, $chromeNew)
}
elseif ($chromeText -notmatch 'Audit delivery must never delay user feedback') {
    throw "The expected Chrome v3.0.2 blocked-download block was not found."
}

Write-Utf8NoBom -Path $chromeWorker -Content $chromeText

if (Test-Path -LiteralPath $firefoxWorker) {
    $firefoxText = [System.IO.File]::ReadAllText($firefoxWorker)

    $firefoxOld = @'
  await stopAndRemoveDownload(downloadItem.id);
  await auditDownload(downloadItem, "blocked", decision);
  await showDownloadBlockedAlert(downloadItem);
'@
    $firefoxNew = @'
  await stopAndRemoveDownload(downloadItem.id);

  // Alert immediately. Audit delivery must never delay user feedback.
  await showDownloadBlockedAlert(downloadItem);
  void auditDownload(downloadItem, "blocked", decision);
'@

    if ($firefoxText.Contains($firefoxOld)) {
        $firefoxText = $firefoxText.Replace($firefoxOld, $firefoxNew)
        Write-Utf8NoBom -Path $firefoxWorker -Content $firefoxText
    }
    elseif ($firefoxText -notmatch 'Audit delivery must never delay user feedback') {
        Write-Warning "Firefox worker did not match the expected v3.0.2 block; Chrome/Edge was still patched."
    }
}

Write-Host "[3/5] Updating unpacked extension version to 3.0.3." -ForegroundColor Cyan

$chrome = Get-Content -LiteralPath $chromeManifest -Raw | ConvertFrom-Json
$chrome.version = "3.0.3"
Write-JsonUtf8NoBom -Path $chromeManifest -Value $chrome

if (Test-Path -LiteralPath $firefoxManifest) {
    $firefox = Get-Content -LiteralPath $firefoxManifest -Raw | ConvertFrom-Json
    $firefox.version = "3.0.3"
    Write-JsonUtf8NoBom -Path $firefoxManifest -Value $firefox
}

Write-Host "[4/5] Verifying source markers." -ForegroundColor Cyan

$installedWorker = [System.IO.File]::ReadAllText($chromeWorker)
$installedManifest = Get-Content -LiteralPath $chromeManifest -Raw | ConvertFrom-Json

if ($installedWorker -notmatch 'Audit delivery must never delay user feedback') {
    throw "The immediate-alert marker was not installed."
}
if ($installedManifest.version -ne "3.0.3") {
    throw "Chrome extension version was not updated to 3.0.3."
}

Write-Host "[5/5] Optional JavaScript syntax validation." -ForegroundColor Cyan
$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -ne $node) {
    & $node.Source --check $chromeWorker
    if ($LASTEXITCODE -ne 0) {
        throw "Chrome service-worker.js failed node --check."
    }

    if (Test-Path -LiteralPath $firefoxWorker) {
        & $node.Source --check $firefoxWorker
        if ($LASTEXITCODE -ne 0) {
            throw "Firefox background script failed node --check."
        }
    }

    Write-Host "JavaScript syntax validation: PASSED" -ForegroundColor Green
}
else {
    Write-Host "Node.js was not found; source-marker validation passed." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Browser download immediate-alert hotfix applied." -ForegroundColor Green
Write-Host "Extension version: 3.0.3" -ForegroundColor Green
Write-Host "No C# source, DLL, EXE, launcher, or Windows Service file was modified." -ForegroundColor Green
Write-Host "No dotnet build is required." -ForegroundColor Green
Write-Host "Backup directory: $backupRoot" -ForegroundColor DarkGray
Write-Host ""
Write-Host "At chrome://extensions or edge://extensions, press Reload on the unpacked Company DLP extension." -ForegroundColor Cyan
