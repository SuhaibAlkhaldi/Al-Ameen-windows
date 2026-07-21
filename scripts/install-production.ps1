#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)] [Guid]$TenantId,
    [Parameter(Mandatory=$true)] [string]$BackendBaseUrl,
    [Parameter(Mandatory=$true)] [string]$PolicySigningPublicKeyPemPath,
    [Parameter(Mandatory=$true)] [string]$ChromeExtensionId,
    [Parameter(Mandatory=$true)] [string]$ChromeExtensionUpdateUrl,
    [Parameter(Mandatory=$true)] [string]$EdgeExtensionId,
    [Parameter(Mandatory=$true)] [string]$EdgeExtensionUpdateUrl
)
$ErrorActionPreference = "Stop"
if ($TenantId -eq [Guid]::Empty) { throw "TenantId must not be empty." }
$backendUri = $null
if (-not [Uri]::TryCreate($BackendBaseUrl, [UriKind]::Absolute, [ref]$backendUri) -or $backendUri.Scheme -ne "https") {
    throw "BackendBaseUrl must be a valid HTTPS URL."
}
if (-not (Test-Path $PolicySigningPublicKeyPemPath)) { throw "Policy signing public-key PEM file was not found." }
$policySigningPublicKeyPem = Get-Content $PolicySigningPublicKeyPemPath -Raw
if ($policySigningPublicKeyPem -notmatch "BEGIN PUBLIC KEY") { throw "Policy signing public-key PEM is invalid." }
$root = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:ProgramFiles "CompanyDlp"
$dataDir = Join-Path $env:ProgramData "CompanyDlp"

Get-Process -Name @("CompanyDlp.Desktop", "CompanyDlp.NativeHost") -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

& (Join-Path $PSScriptRoot "publish.ps1")
& (Join-Path $PSScriptRoot "assert-production-signatures.ps1") -PublishRoot (Join-Path $root "artifacts\publish")
& (Join-Path $PSScriptRoot "production-browser-policy-backup.ps1") -Mode Backup

if (Get-Service CompanyDlp -ErrorAction SilentlyContinue) {
    Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue
    sc.exe delete CompanyDlp | Out-Null
    Start-Sleep -Seconds 1
}

Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $installDir -ItemType Directory -Force | Out-Null
New-Item $dataDir -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $root "artifacts\publish\*") $installDir -Recurse -Force

$policy = Get-Content (Join-Path $root "config\policy.production.sample.json") -Raw | ConvertFrom-Json
$policy.browser.chromeExtensionId = $ChromeExtensionId
$policy.browser.chromeExtensionUpdateUrl = $ChromeExtensionUpdateUrl
$policy.browser.edgeExtensionId = $EdgeExtensionId
$policy.browser.edgeExtensionUpdateUrl = $EdgeExtensionUpdateUrl
$policy.backend.tenantId = $TenantId
$policy.backend.baseUrl = $backendUri.AbsoluteUri.TrimEnd('/')
$policy.backend.policySigningPublicKeyPem = $policySigningPublicKeyPem
$policy.backend.authenticationMode = "DeviceBearerToken"
$policy | ConvertTo-Json -Depth 20 | Set-Content (Join-Path $dataDir "policy.json") -Encoding UTF8

icacls $installDir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "Users:(OI)(CI)RX" /T /C | Out-Null
icacls $dataDir /inheritance:r /grant:r "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" /T /C | Out-Null

$serviceExe = Join-Path $installDir "Service\CompanyDlp.Service.exe"
sc.exe create CompanyDlp binPath= "`"$serviceExe`"" start= auto DisplayName= "Company DLP Service" | Out-Null
sc.exe description CompanyDlp "Company endpoint data loss prevention service" | Out-Null

$desktopExe = Join-Path $installDir "Desktop\CompanyDlp.Desktop.exe"

& (Join-Path $PSScriptRoot "register-production-context-menu.ps1") -DesktopExe $desktopExe

& (Join-Path $PSScriptRoot "register-native-host-production.ps1") `
    -NativeHostExe (Join-Path $installDir "NativeHost\CompanyDlp.NativeHost.exe") `
    -ExtensionIds @($ChromeExtensionId, $EdgeExtensionId)

& (Join-Path $PSScriptRoot "register-browser-force-install.ps1") `
    -ChromeExtensionId $ChromeExtensionId `
    -ChromeExtensionUpdateUrl $ChromeExtensionUpdateUrl `
    -EdgeExtensionId $EdgeExtensionId `
    -EdgeExtensionUpdateUrl $EdgeExtensionUpdateUrl

[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", "Production", "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", (Join-Path $dataDir "policy.json"), "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $desktopExe, "Machine")

Start-Service CompanyDlp
Write-Host "Production installation completed." -ForegroundColor Green
Write-Host "Disconnect all non-input external USB devices before the first service start/baseline on a client image." -ForegroundColor Yellow
