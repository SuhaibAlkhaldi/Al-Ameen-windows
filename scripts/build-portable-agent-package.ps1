#Requires -RunAsAdministrator
# Run this ONCE, on a build machine that has the .NET SDK and your code-signing certificate
# installed (not on employee machines). It builds, signs, and assembles everything into one folder
# you copy to a flash drive - after that, installing on any target machine is just
# scripts\deploy-agent-portable.ps1 (or double-clicking Install-CompanyDlp.bat), no SDK/cert needed
# on the target machine, and it will ask for a one-time enrollment code interactively.
#
# -RootCertPath is OPTIONAL and only needed if -CertThumbprint refers to a self-signed
# code-signing certificate rather than one issued by a real CA (DigiCert/Sectigo/etc). A real
# CA-issued cert is already trusted by every Windows machine out of the box, so nothing extra is
# needed. A self-signed cert is NOT trusted anywhere by default - not even on this build machine -
# so if you're using one, first export its public portion to a .cer file and pass that path here.
# It gets bundled into the package, and deploy-agent-portable.ps1 auto-trusts it on each target
# machine at install time (it already runs elevated, so this needs no separate manual/GPO step).
param(
    [Parameter(Mandatory=$true)] [string]$CertThumbprint,
    [Parameter(Mandatory=$true)] [Guid]$TenantId,
    [Parameter(Mandatory=$true)] [string]$BackendBaseUrl,
    [Parameter(Mandatory=$true)] [string]$PolicySigningPublicKeyPemPath,
    [Parameter(Mandatory=$true)] [string]$ChromeExtensionId,
    [Parameter(Mandatory=$true)] [string]$ChromeExtensionUpdateUrl,
    [Parameter(Mandatory=$true)] [string]$EdgeExtensionId,
    [Parameter(Mandatory=$true)] [string]$EdgeExtensionUpdateUrl,
    [string]$RootCertPath,
    [string]$TimestampServer = "http://timestamp.digicert.com",
    [string]$OutputDirectory = "artifacts\portable-package"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$backendUri = $null
if (-not [Uri]::TryCreate($BackendBaseUrl, [UriKind]::Absolute, [ref]$backendUri) -or $backendUri.Scheme -ne "https") {
    throw "BackendBaseUrl must be a valid HTTPS URL."
}
if (-not (Test-Path $PolicySigningPublicKeyPemPath)) { throw "Policy signing public-key PEM file was not found." }
$policySigningPublicKeyPem = Get-Content $PolicySigningPublicKeyPemPath -Raw
if ($policySigningPublicKeyPem -notmatch "BEGIN PUBLIC KEY") { throw "Policy signing public-key PEM is invalid." }

$cert = @(Get-ChildItem Cert:\CurrentUser\My, Cert:\LocalMachine\My -CodeSigningCert -ErrorAction SilentlyContinue) |
    Where-Object { $_.Thumbprint -eq $CertThumbprint } | Select-Object -First 1
if (-not $cert) {
    throw "No code-signing certificate with thumbprint '$CertThumbprint' was found in Cert:\CurrentUser\My or Cert:\LocalMachine\My. Import it (or connect the hardware token) first."
}

if ($RootCertPath) {
    if (-not (Test-Path $RootCertPath)) { throw "RootCertPath '$RootCertPath' was not found." }

    # A self-signed cert isn't trusted anywhere by default, including here - trust it on this build
    # machine too, otherwise assert-production-signatures.ps1 below will fail on its own output.
    Write-Host "Trusting self-signed certificate on this build machine..." -ForegroundColor Cyan
    $rootCertToTrust = Get-PfxCertificate -FilePath $RootCertPath
    $alreadyTrustedLocally = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $rootCertToTrust.Thumbprint }
    if (-not $alreadyTrustedLocally) {
        Import-Certificate -FilePath $RootCertPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    }
}

Write-Host "Building..." -ForegroundColor Cyan
& (Join-Path $PSScriptRoot "publish.ps1")

$publishRoot = Join-Path $root "artifacts\publish"
Write-Host "Signing binaries with certificate '$($cert.Subject)'..." -ForegroundColor Cyan
Get-ChildItem $publishRoot -Recurse -Include *.exe, *.dll | ForEach-Object {
    $signature = Set-AuthenticodeSignature -FilePath $_.FullName -Certificate $cert -HashAlgorithm SHA256 -TimestampServer $TimestampServer
    if ($signature.Status -ne "Valid") { throw "Failed to sign $($_.FullName): $($signature.StatusMessage)" }
}
Write-Host "Signed $(  (Get-ChildItem $publishRoot -Recurse -Include *.exe,*.dll).Count ) files." -ForegroundColor Green

& (Join-Path $PSScriptRoot "assert-production-signatures.ps1") -PublishRoot $publishRoot

$outDir = Join-Path $root $OutputDirectory
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $outDir -ItemType Directory -Force | Out-Null
Copy-Item $publishRoot (Join-Path $outDir "publish") -Recurse -Force

$scriptsOut = Join-Path $outDir "Scripts"
New-Item $scriptsOut -ItemType Directory -Force | Out-Null
foreach ($name in @(
    "register-production-context-menu.ps1",
    "register-shell-extension-production.ps1",
    "register-native-host-production.ps1",
    "register-browser-force-install.ps1"
)) {
    Copy-Item (Join-Path $PSScriptRoot $name) (Join-Path $scriptsOut $name) -Force
}

Copy-Item (Join-Path $PSScriptRoot "deploy-agent-portable.ps1") (Join-Path $outDir "deploy-agent-portable.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "Install-CompanyDlp.bat") (Join-Path $outDir "Install-CompanyDlp.bat") -Force

if ($RootCertPath) {
    Copy-Item $RootCertPath (Join-Path $outDir "CompanyDlpCodeSigningRoot.cer") -Force
    Write-Host "Bundled self-signed root certificate - deploy-agent-portable.ps1 will auto-trust it on each target machine." -ForegroundColor Cyan
}

$config = [ordered]@{
    tenantId                  = $TenantId.ToString()
    backendBaseUrl             = $backendUri.AbsoluteUri.TrimEnd('/')
    policySigningPublicKeyPem = $policySigningPublicKeyPem
    chromeExtensionId          = $ChromeExtensionId
    chromeExtensionUpdateUrl   = $ChromeExtensionUpdateUrl
    edgeExtensionId             = $EdgeExtensionId
    edgeExtensionUpdateUrl      = $EdgeExtensionUpdateUrl
}
$config | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $outDir "portable-config.json") -Encoding UTF8

Write-Host ""
Write-Host "Portable package ready: $outDir" -ForegroundColor Green
Write-Host "Copy this whole folder to a flash drive. On any target machine: right-click Install-CompanyDlp.bat -> Run as administrator." -ForegroundColor Cyan
Write-Host "It will ask for a one-time enrollment code partway through - have one ready (POST /api/v1/device-enrollment-tokens)." -ForegroundColor Cyan
