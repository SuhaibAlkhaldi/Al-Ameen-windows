#Requires -RunAsAdministrator
# Portable, no-rebuild agent installer - run this directly from a flash drive (or any folder) on
# any target Windows machine. Unlike install-production.ps1, this never calls publish.ps1 - it
# expects the binaries next to it to already be built AND signed (see
# scripts\build-portable-agent-package.ps1, which produces exactly this layout). That's what makes
# this safe to run on an employee's machine, which won't have the .NET SDK or a code-signing
# certificate installed.
#
# Expected layout next to this script:
#   .\publish\                 (already-built, already-signed artifacts\publish output)
#   .\Scripts\                 (copies of the register-*.ps1 helper scripts)
#   .\portable-config.json     (tenantId/backendBaseUrl/policy key/extension ids - filled in once
#                                when the package was built)
param(
    [string]$ConfigPath = (Join-Path $PSScriptRoot "portable-config.json")
)
$ErrorActionPreference = "Stop"

if (-not (Test-Path $ConfigPath)) {
    throw "portable-config.json was not found next to this script. This flash drive was not built with scripts\build-portable-agent-package.ps1."
}
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

$tenantId = [Guid]$config.tenantId
if ($tenantId -eq [Guid]::Empty) { throw "portable-config.json: tenantId must not be empty." }
$backendUri = $null
if (-not [Uri]::TryCreate($config.backendBaseUrl, [UriKind]::Absolute, [ref]$backendUri) -or $backendUri.Scheme -ne "https") {
    throw "portable-config.json: backendBaseUrl must be a valid HTTPS URL."
}
$policySigningPublicKeyPem = $config.policySigningPublicKeyPem
if ($policySigningPublicKeyPem -notmatch "BEGIN PUBLIC KEY") { throw "portable-config.json: policySigningPublicKeyPem is invalid." }

$publishRoot = Join-Path $PSScriptRoot "publish"
$scriptsRoot = Join-Path $PSScriptRoot "Scripts"
if (-not (Test-Path $publishRoot)) { throw "publish\ folder was not found next to this script - this flash drive is incomplete." }
if (-not (Test-Path $scriptsRoot)) { throw "Scripts\ folder was not found next to this script - this flash drive is incomplete." }

# Same signature check install-production.ps1 does - doesn't need the SDK, just Get-AuthenticodeSignature
# (built into PowerShell), and confirms the binaries on this flash drive are still genuinely signed
# and untampered before touching this machine at all.
Write-Host "Checking binary signatures..." -ForegroundColor Cyan
$files = Get-ChildItem $publishRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll") }
if (-not $files) { throw "No production binaries were found under $publishRoot." }
$invalid = foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature $file.FullName
    if ($signature.Status -ne "Valid") {
        [PSCustomObject]@{ File = $file.FullName; Status = $signature.Status; Message = $signature.StatusMessage }
    }
}
if ($invalid) {
    $invalid | Format-Table -AutoSize -Wrap
    throw "Stopped: every EXE and DLL on this flash drive must have a valid trusted Authenticode signature."
}
Write-Host "Signatures OK." -ForegroundColor Green

$installDir = Join-Path $env:ProgramFiles "CompanyDlp"
$dataDir = Join-Path $env:ProgramData "CompanyDlp"

Get-Process -Name @("CompanyDlp.Desktop", "CompanyDlp.NativeHost") -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

if (Get-Service CompanyDlp -ErrorAction SilentlyContinue) {
    Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue
    sc.exe delete CompanyDlp | Out-Null
    Start-Sleep -Seconds 1
}

Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item $installDir -ItemType Directory -Force | Out-Null
New-Item $dataDir -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $publishRoot "*") $installDir -Recurse -Force

$policyTemplatePath = Join-Path $publishRoot "policy.production.sample.json"
if (-not (Test-Path $policyTemplatePath)) {
    throw "policy.production.sample.json was not found under publish\ - this flash drive is incomplete."
}
$policy = Get-Content $policyTemplatePath -Raw | ConvertFrom-Json
$policy.browser.chromeExtensionId = $config.chromeExtensionId
$policy.browser.chromeExtensionUpdateUrl = $config.chromeExtensionUpdateUrl
$policy.browser.edgeExtensionId = $config.edgeExtensionId
$policy.browser.edgeExtensionUpdateUrl = $config.edgeExtensionUpdateUrl
$policy.backend.tenantId = $tenantId
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

& (Join-Path $scriptsRoot "register-production-context-menu.ps1") -DesktopExe $desktopExe
& (Join-Path $scriptsRoot "register-shell-extension-production.ps1") -RegisterExe (Join-Path $installDir "ShellExtension\CompanyDlp.ShellExtension.Register.exe")
& (Join-Path $scriptsRoot "register-native-host-production.ps1") -NativeHostExe (Join-Path $installDir "NativeHost\CompanyDlp.NativeHost.exe") -ExtensionIds @($config.chromeExtensionId, $config.edgeExtensionId)
& (Join-Path $scriptsRoot "register-browser-force-install.ps1") -ChromeExtensionId $config.chromeExtensionId -ChromeExtensionUpdateUrl $config.chromeExtensionUpdateUrl -EdgeExtensionId $config.edgeExtensionId -EdgeExtensionUpdateUrl $config.edgeExtensionUpdateUrl

[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", "Production", "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", (Join-Path $dataDir "policy.json"), "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $desktopExe, "Machine")

Write-Host ""
Write-Host "Files installed. This device now needs a one-time enrollment code (create one per device," -ForegroundColor Cyan
Write-Host "or reuse a multi-use batch code, from the admin portal / POST /api/v1/device-enrollment-tokens)." -ForegroundColor Cyan
$enrollmentCode = Read-Host "Enrollment code"
if ([string]::IsNullOrWhiteSpace($enrollmentCode)) { throw "An enrollment code is required to finish setup - re-run this script once you have one." }

$env:COMPANY_DLP_ENROLLMENT_CODE = $enrollmentCode
try {
    & $serviceExe --enroll
} finally {
    Remove-Item Env:COMPANY_DLP_ENROLLMENT_CODE -ErrorAction SilentlyContinue
    $enrollmentCode = $null
}

Start-Service CompanyDlp
Write-Host ""
Write-Host "Company DLP agent installed and enrolled on this device." -ForegroundColor Green
Write-Host "Disconnect all non-input external USB devices before first real use on a client image." -ForegroundColor Yellow
