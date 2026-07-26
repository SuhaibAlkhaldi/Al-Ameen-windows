[CmdletBinding()]
param(
    [switch]$SkipInstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$portal = Join-Path $root "src\CompanyDlp.AdminPortal"
if (-not (Test-Path (Join-Path $portal "package.json"))) { throw "Admin Portal was not found: $portal" }

Push-Location $portal
try {
    if (-not $SkipInstall -and -not (Test-Path (Join-Path $portal "node_modules"))) {
        Write-Host "Installing Company DLP Admin Portal dependencies..." -ForegroundColor Cyan
        & npm.cmd install
        if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE." }
    }

    Write-Host "Starting Company DLP Admin Portal at http://127.0.0.1:4200" -ForegroundColor Cyan
    & npm.cmd start
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
