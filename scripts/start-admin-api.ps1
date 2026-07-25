[CmdletBinding()]
param(
    [string]$Url = "http://127.0.0.1:5060"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:COMPANY_DLP_ADMIN_URL = $Url

$exitCode = 1
Push-Location $root
try {
    Write-Host "Starting Company DLP Admin API at $Url" -ForegroundColor Cyan
    Write-Host "Development SQL database: (localdb)\MSSQLLocalDB / CompanyDlpAdminDevelopment" -ForegroundColor DarkGray
    & dotnet run --project (Join-Path $root "src\CompanyDlp.AdminApi\CompanyDlp.AdminApi.csproj")
    $exitCode = $LASTEXITCODE
}
finally { Pop-Location }
exit $exitCode
