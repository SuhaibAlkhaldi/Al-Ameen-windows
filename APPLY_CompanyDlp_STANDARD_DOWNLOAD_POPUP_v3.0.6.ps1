param(
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$workerPath = Join-Path $root "browser-extension\service-worker.js"
$manifestPath = Join-Path $root "browser-extension\manifest.json"
$contentPath = Join-Path $root "browser-extension\content.js"
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"

foreach ($required in @($workerPath, $manifestPath, $contentPath)) {
    if (-not (Test-Path -LiteralPath $required)) {
        throw "Required file was not found: $required"
    }
}

$content = [System.IO.File]::ReadAllText($contentPath)
if ($content -notmatch 'message\?\.type !== "showDlpAlert"') {
    throw "The existing Company DLP in-page alert listener was not found in content.js."
}

Copy-Item -LiteralPath $workerPath `
    -Destination "$workerPath.before-standard-download-alert-$timestamp.bak" `
    -Force

Copy-Item -LiteralPath $manifestPath `
    -Destination "$manifestPath.before-standard-download-alert-$timestamp.bak" `
    -Force

$worker = [System.IO.File]::ReadAllText($workerPath)
$handleMarker = "async function handleCreatedDownload(downloadItem) {"
$listenerMarker = "chrome.downloads.onCreated.addListener"

$helper = @'
function sendDlpAlertMessage(tabId, payload) {
  return new Promise((resolve) => {
    try {
      chrome.tabs.sendMessage(tabId, payload, () => {
        resolve(!chrome.runtime.lastError);
      });
    } catch (_) {
      resolve(false);
    }
  });
}

function injectCompanyDlpContentScript(tabId) {
  return new Promise((resolve) => {
    try {
      chrome.scripting.executeScript(
        {
          target: { tabId },
          files: ["content.js"]
        },
        () => resolve(!chrome.runtime.lastError)
      );
    } catch (_) {
      resolve(false);
    }
  });
}

function getActiveDownloadTabId(downloadItem) {
  if (Number.isInteger(downloadItem?.tabId) && downloadItem.tabId >= 0) {
    return Promise.resolve(downloadItem.tabId);
  }

  return new Promise((resolve) => {
    try {
      chrome.tabs.query(
        {
          active: true,
          lastFocusedWindow: true
        },
        (tabs) => {
          if (chrome.runtime.lastError) {
            resolve(null);
            return;
          }

          const tabId = tabs?.[0]?.id;
          resolve(Number.isInteger(tabId) ? tabId : null);
        }
      );
    } catch (_) {
      resolve(null);
    }
  });
}

async function showStandardDlpDownloadAlert(downloadItem) {
  const tabId = await getActiveDownloadTabId(downloadItem);
  if (!Number.isInteger(tabId)) return false;

  const payload = {
    type: "showDlpAlert",
    title: "Download blocked",
    message: "Downloading files through this browser is not allowed by company security policy."
  };

  if (await sendDlpAlertMessage(tabId, payload)) {
    return true;
  }

  // The tab may have been open before the unpacked extension was reloaded.
  // Inject the existing Company DLP content script, then retry the same alert.
  if (!await injectCompanyDlpContentScript(tabId)) {
    return false;
  }

  return await sendDlpAlertMessage(tabId, payload);
}

'@

if ($worker -notmatch 'async function showStandardDlpDownloadAlert') {
    $markerIndex = $worker.IndexOf($handleMarker, [System.StringComparison]::Ordinal)
    if ($markerIndex -lt 0) {
        throw "handleCreatedDownload was not found in service-worker.js."
    }

    $worker = $worker.Insert($markerIndex, $helper)
}

$handleStart = $worker.IndexOf($handleMarker, [System.StringComparison]::Ordinal)
$listenerStart = $worker.IndexOf($listenerMarker, $handleStart, [System.StringComparison]::Ordinal)

if ($handleStart -lt 0 -or $listenerStart -lt 0) {
    throw "Could not locate the browser download handler."
}

$handleBlock = $worker.Substring($handleStart, $listenerStart - $handleStart)
$denyStart = $handleBlock.LastIndexOf(
    "  await stopAndRemoveDownload(downloadItem.id);",
    [System.StringComparison]::Ordinal
)

if ($denyStart -lt 0) {
    throw "The blocked-download branch was not found."
}

$handlePrefix = $handleBlock.Substring(0, $denyStart)

$blockedTail = @'
  await stopAndRemoveDownload(downloadItem.id);

  // Reuse the exact same red in-page Company DLP popup used by upload,
  // drag/drop, paste, and the other browser actions.
  await showStandardDlpDownloadAlert(downloadItem);

  // Audit must never delay the visible popup.
  void auditDownload(downloadItem, "blocked", decision);
}

'@

$worker = $worker.Substring(0, $handleStart) +
    $handlePrefix +
    $blockedTail +
    $worker.Substring($listenerStart)

$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($workerPath, $worker, $utf8)

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$permissions = @($manifest.permissions)

if ($permissions -notcontains "scripting") {
    $permissions += "scripting"
}

$manifest.permissions = $permissions
$manifest.version = "3.0.6"

[System.IO.File]::WriteAllText(
    $manifestPath,
    ($manifest | ConvertTo-Json -Depth 100),
    $utf8
)

$installedWorker = [System.IO.File]::ReadAllText($workerPath)
$installedManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($installedWorker -notmatch 'showStandardDlpDownloadAlert') {
    throw "The standard Company DLP download alert was not installed."
}

if ($installedManifest.version -ne "3.0.6") {
    throw "The extension version was not updated to 3.0.6."
}

if (@($installedManifest.permissions) -notcontains "scripting") {
    throw "The scripting permission was not added."
}

$node = Get-Command node -ErrorAction SilentlyContinue
if ($null -ne $node) {
    & $node.Source --check $workerPath
    if ($LASTEXITCODE -ne 0) {
        throw "service-worker.js failed JavaScript syntax validation."
    }
}

Write-Host ""
Write-Host "Company DLP standard download popup applied successfully." -ForegroundColor Green
Write-Host "Extension version: 3.0.6" -ForegroundColor Green
Write-Host "No DLL, EXE, Service, launcher, or dotnet build was changed." -ForegroundColor Green
Write-Host ""
Write-Host "Open chrome://extensions or edge://extensions and press Reload." -ForegroundColor Cyan
Write-Host "Then refresh the test page with Ctrl+Shift+R before testing." -ForegroundColor Cyan
