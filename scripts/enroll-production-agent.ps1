#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)]
    [string]$EnrollmentCode,
    [string]$ServiceExe = "$env:ProgramFiles\CompanyDlp\Service\CompanyDlp.Service.exe"
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path $ServiceExe)) { throw "Company DLP service executable was not found: $ServiceExe" }
$signature = Get-AuthenticodeSignature $ServiceExe
if ($signature.Status -ne "Valid") { throw "Enrollment refused because the Company DLP service signature is not valid: $($signature.Status)" }

$previous = $env:COMPANY_DLP_ENROLLMENT_CODE
try {
    $env:COMPANY_DLP_ENROLLMENT_CODE = $EnrollmentCode
    & $ServiceExe --enroll
    if ($LASTEXITCODE -ne 0) { throw "Agent enrollment failed with exit code $LASTEXITCODE." }
}
finally {
    $env:COMPANY_DLP_ENROLLMENT_CODE = $previous
}

Write-Host "Company DLP device enrollment completed. The token is DPAPI-protected under ProgramData." -ForegroundColor Green
