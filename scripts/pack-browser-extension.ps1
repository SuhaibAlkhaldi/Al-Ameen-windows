#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)] [string]$PrivateKeyPath,
    [string]$Version,
    [string]$ChromeExePath
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $PrivateKeyPath)) { throw "Private signing key was not found: $PrivateKeyPath" }

$root = Split-Path -Parent $PSScriptRoot
$extensionDir = Join-Path $root "browser-extension"
$manifestPath = Join-Path $extensionDir "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest.json was not found: $manifestPath" }

# Bump the manifest version so every pack produces a distinguishable, monotonically
# increasing version that the update manifest (update.xml) can advertise.
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
if (-not $Version) {
    $parts = @($manifest.version -split '\.')
    while ($parts.Count -lt 3) { $parts += "0" }
    $parts[-1] = [string]([int]$parts[-1] + 1)
    $Version = $parts -join '.'
}
$manifest.version = $Version
$json = $manifest | ConvertTo-Json -Depth 20
# ConvertTo-Json HTML-escapes '<', '>', '&', "'" by default (e.g. "<all_urls>" -> "<all_urls>").
# Unescape them so repeated packs don't progressively mangle the manifest's readable JSON.
$json = $json -replace '\\u003c', '<' -replace '\\u003e', '>' -replace '\\u0026', '&' -replace '\\u0027', "'"
Set-Content $manifestPath -Value $json -Encoding UTF8
Write-Host "browser-extension/manifest.json version set to $Version" -ForegroundColor Cyan

if (-not $ChromeExePath) {
    $candidates = @(
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe")
    )
    $ChromeExePath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $ChromeExePath) { throw "Chrome executable was not found. Pass -ChromeExePath explicitly." }
}

$outDir = Join-Path $root "artifacts\extension"
New-Item $outDir -ItemType Directory -Force | Out-Null

# chrome.exe --pack-extension writes "<source-dir-name>.crx" next to the source directory itself.
$producedCrx = Join-Path $root "browser-extension.crx"
Remove-Item $producedCrx -Force -ErrorAction SilentlyContinue

& $ChromeExePath "--pack-extension=$extensionDir" "--pack-extension-key=$PrivateKeyPath" "--no-sandbox" | Out-Null
Start-Sleep -Seconds 1

if (-not (Test-Path $producedCrx)) {
    throw "chrome.exe did not produce $producedCrx. Close any running Chrome instances and confirm the private key matches browser-extension/manifest.json's 'key' field."
}

$finalCrx = Join-Path $outDir "company-dlp.crx"
Move-Item $producedCrx $finalCrx -Force

# Derive the extension ID the same way Chrome/Edge does: SHA-256 of the manifest's DER-encoded
# public key ("key" field), first 16 bytes, each nibble mapped to 'a'-'p' instead of a hex digit.
# Computing it here (rather than as a separate manual step) keeps it from ever drifting out of
# sync with the key actually used to sign this specific .crx.
$publicKeyDer = [Convert]::FromBase64String($manifest.key)
$hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($publicKeyDer)
$idChars = New-Object System.Text.StringBuilder
foreach ($b in $hash[0..15]) {
    [void]$idChars.Append([char](97 + [int]($b -shr 4)))
    [void]$idChars.Append([char](97 + [int]($b -band 0xF)))
}
$extensionId = $idChars.ToString()

Write-Host "Packed browser-extension -> $finalCrx (version $Version, extension id $extensionId)" -ForegroundColor Green
[pscustomobject]@{ CrxPath = $finalCrx; Version = $Version; ExtensionId = $extensionId }
