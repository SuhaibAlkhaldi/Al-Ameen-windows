#Requires -Version 5.1
<#
.SYNOPSIS
Packs firefox-extension/ into a .xpi, and (only if Mozilla API credentials are supplied) signs it
through Mozilla's Add-on signing service as an "unlisted" add-on.

.DESCRIPTION
Firefox Release refuses to install an unsigned .xpi even via enterprise policy
(SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings, force_installed) - this is a hard Firefox
restriction, not something register-browser-force-install.ps1 or BrowserPolicyManager.cs can work
around. The only supported way to get a self-hosted (not AMO-store-listed) .xpi that Firefox will
actually load is Mozilla's Add-on signing service using the "unlisted" distribution channel: signed,
but never published to addons.mozilla.org's public search/listing.

ONE-TIME MANUAL SETUP (cannot be automated - requires a real Mozilla account):
  1. Create (or use an existing) account at https://addons.mozilla.org.
  2. Go to https://addons.mozilla.org/developers/addon/api/key/ and generate API credentials -
     this gives you a JWT issuer ("API key") and JWT secret ("API secret").
  3. Pass them to this script as -ApiKey / -ApiSecret (or set the AMO_JWT_ISSUER / AMO_JWT_SECRET
     environment variables) to have it sign automatically; omit both to just produce the unsigned
     .xpi and sign it yourself later via the same `web-ext sign` command this script prints.

This step was NOT performed in this change - it requires a real Mozilla Developer Hub account, which
this session does not have access to. Everything up to producing the unsigned .xpi and constructing the
signing command was implemented and is ready to run; the actual sign step is a one-time manual action
for whoever owns (or creates) the Mozilla account this extension will be signed under.

.PARAMETER Version
Overrides firefox-extension/manifest.json's version instead of auto-bumping the patch number (mirrors
pack-browser-extension.ps1's behavior for the Chrome/Edge side).

.PARAMETER ApiKey
Mozilla AMO JWT issuer ("API key"). Falls back to $env:AMO_JWT_ISSUER. Omit (with -ApiSecret) to skip
signing and only produce the unsigned .xpi.

.PARAMETER ApiSecret
Mozilla AMO JWT secret ("API secret"). Falls back to $env:AMO_JWT_SECRET.
#>
param(
    [string]$Version,
    [string]$ApiKey = $env:AMO_JWT_ISSUER,
    [string]$ApiSecret = $env:AMO_JWT_SECRET
)
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$extensionDir = Join-Path $root "firefox-extension"
$manifestPath = Join-Path $extensionDir "manifest.json"
if (-not (Test-Path $manifestPath)) { throw "manifest.json was not found: $manifestPath" }

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$extensionId = $manifest.browser_specific_settings.gecko.id
if (-not $extensionId) { throw "firefox-extension/manifest.json is missing browser_specific_settings.gecko.id." }

if (-not $Version) {
    $parts = @($manifest.version -split '\.')
    while ($parts.Count -lt 3) { $parts += "0" }
    $parts[-1] = [string]([int]$parts[-1] + 1)
    $Version = $parts -join '.'
}
$manifest.version = $Version
$json = $manifest | ConvertTo-Json -Depth 20
# ConvertTo-Json HTML-escapes '<', '>', '&', "'" by default - unescape so repeated packs don't
# progressively mangle the manifest's readable JSON (same fix pack-browser-extension.ps1 applies).
$json = $json -replace '\\u003c', '<' -replace '\\u003e', '>' -replace '\\u0026', '&' -replace '\\u0027', "'"
Set-Content $manifestPath -Value $json -Encoding UTF8
Write-Host "firefox-extension/manifest.json version set to $Version" -ForegroundColor Cyan

$outDir = Join-Path $root "artifacts\extension"
New-Item $outDir -ItemType Directory -Force | Out-Null
$unsignedXpiPath = Join-Path $outDir "company-dlp-firefox-unsigned.xpi"
Remove-Item $unsignedXpiPath -Force -ErrorAction SilentlyContinue

# A .xpi is just a zip of the extension source directory's contents (not the directory itself).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$tempZip = Join-Path $outDir "company-dlp-firefox-unsigned.zip"
Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory($extensionDir, $tempZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item $tempZip $unsignedXpiPath -Force

Write-Host "Packed firefox-extension -> $unsignedXpiPath (version $Version, extension id $extensionId)" -ForegroundColor Green

if (-not $ApiKey -or -not $ApiSecret) {
    Write-Host ""
    Write-Host "No Mozilla API credentials supplied (-ApiKey/-ApiSecret or AMO_JWT_ISSUER/AMO_JWT_SECRET) -" -ForegroundColor Yellow
    Write-Host "the .xpi above is UNSIGNED and Firefox Release will refuse to install it, even via" -ForegroundColor Yellow
    Write-Host "enterprise policy. To sign it as an unlisted add-on (see this script's header comment" -ForegroundColor Yellow
    Write-Host "for the one-time account setup this needs), run:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  npx web-ext sign --source-dir `"$extensionDir`" --artifacts-dir `"$outDir`" --channel unlisted --api-key <your-jwt-issuer> --api-secret <your-jwt-secret>" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "web-ext writes the signed .xpi into $outDir - pass that file to publish.ps1 -FirefoxSignedXpiPath." -ForegroundColor Yellow
    [pscustomobject]@{ UnsignedXpiPath = $unsignedXpiPath; Version = $Version; ExtensionId = $extensionId; Signed = $false }
    return
}

Write-Host "Signing via Mozilla Add-on signing (channel: unlisted)..." -ForegroundColor Cyan
$signArgs = @(
    "web-ext", "sign",
    "--source-dir", $extensionDir,
    "--artifacts-dir", $outDir,
    "--channel", "unlisted",
    "--api-key", $ApiKey,
    "--api-secret", $ApiSecret
)
& npx @signArgs
if ($LASTEXITCODE -ne 0) { throw "web-ext sign failed (exit code $LASTEXITCODE)." }

$signedXpi = Get-ChildItem $outDir -Filter "*.xpi" | Where-Object { $_.Name -ne (Split-Path $unsignedXpiPath -Leaf) } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $signedXpi) { throw "web-ext sign reported success but no signed .xpi was found in $outDir." }

Write-Host "Signed .xpi -> $($signedXpi.FullName)" -ForegroundColor Green
[pscustomobject]@{ SignedXpiPath = $signedXpi.FullName; Version = $Version; ExtensionId = $extensionId; Signed = $true }
