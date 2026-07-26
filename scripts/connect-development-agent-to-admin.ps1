[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [Guid]$TenantId,
    [string]$BaseUrl = "http://127.0.0.1:5060"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$policyPath = Join-Path $root "config\policy.development.json"
if (-not (Test-Path $policyPath)) { throw "Development policy was not found: $policyPath" }

$secureCode = Read-Host "One-time enrollment code" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureCode)
try {
    $enrollmentCode = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $backup = "$policyPath.before-central-api-$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"
    Copy-Item $policyPath $backup -Force
    $policy = Get-Content $policyPath -Raw | ConvertFrom-Json
    $policy.backend.enabled = $true
    $policy.backend.tenantId = $TenantId.ToString("D")
    $policy.backend.mode = "Development"
    $policy.backend.baseUrl = $BaseUrl.TrimEnd('/')
    $policy.backend.authenticationMode = "DeviceBearerToken"
    $policy.backend.allowUnsignedDevelopmentPolicy = $true
    if (-not ($policy.backend.PSObject.Properties.Name -contains "heartbeatSeconds")) {
        $policy.backend | Add-Member -NotePropertyName heartbeatSeconds -NotePropertyValue 15
    } else {
        $policy.backend.heartbeatSeconds = 15
    }
    $policy.backend.policySyncSeconds = 30
    $policy | ConvertTo-Json -Depth 100 | Set-Content $policyPath -Encoding UTF8

    $env:COMPANY_DLP_PROJECT_ROOT = $root
    $env:COMPANY_DLP_POLICY_PATH = $policyPath
    $env:COMPANY_DLP_MODE = "Development"
    $env:COMPANY_DLP_ENROLLMENT_CODE = $enrollmentCode
    Write-Host "Policy backup: $backup" -ForegroundColor DarkGray
    & dotnet run --project (Join-Path $root "src\CompanyDlp.Service\CompanyDlp.Service.csproj") -- --enroll
    if ($LASTEXITCODE -ne 0) { throw "Agent enrollment failed with exit code $LASTEXITCODE." }
    Write-Host "Development agent is enrolled with the central Admin API." -ForegroundColor Green
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $env:COMPANY_DLP_ENROLLMENT_CODE = $null
    $enrollmentCode = $null
}
