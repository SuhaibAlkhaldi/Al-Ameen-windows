[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'CompanyDlp.sln'
$portal = Join-Path $root 'src\CompanyDlp.AdminPortal'

function Require-Command {
    param([Parameter(Mandatory)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

Require-Command dotnet
Require-Command node
Require-Command npm

Write-Host "[1/5] .NET SDK" -ForegroundColor Cyan
dotnet --info

Write-Host "[2/5] Restore .NET solution" -ForegroundColor Cyan
dotnet restore $solution
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

Write-Host "[3/5] Build .NET solution ($Configuration)" -ForegroundColor Cyan
dotnet build $solution --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

Write-Host "[4/5] Run .NET tests" -ForegroundColor Cyan
dotnet test (Join-Path $root 'tests\CompanyDlp.Tests\CompanyDlp.Tests.csproj') --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }

Write-Host "[5/5] Clean-install and build Angular Admin Portal" -ForegroundColor Cyan
Push-Location $portal
try {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -eq $npmCommand) {
        $npmCommand = Get-Command npm -ErrorAction Stop
    }
    $npmExecutable = if (-not [string]::IsNullOrWhiteSpace($npmCommand.Source)) {
        $npmCommand.Source
    }
    else {
        $npmCommand.Path
    }

    $npmCiArgs = @('ci', '--registry=https://registry.npmjs.org/', '--no-audit', '--no-fund')
    & $npmExecutable @npmCiArgs
    $npmCiExitCode = $LASTEXITCODE

    if ($npmCiExitCode -ne 0) {
        Write-Warning 'npm ci failed. Retrying a clean install with npm install as a workaround for the npm CLI exit-handler bug.'
        $nodeModules = Join-Path $portal 'node_modules'
        if (Test-Path -LiteralPath $nodeModules) {
            Remove-Item -LiteralPath $nodeModules -Recurse -Force
        }

        $npmInstallArgs = @('install', '--registry=https://registry.npmjs.org/', '--no-audit', '--no-fund')
        & $npmExecutable @npmInstallArgs
        $npmInstallExitCode = $LASTEXITCODE
        if ($npmInstallExitCode -ne 0) {
            throw 'Angular dependency installation failed with both npm ci and npm install.'
        }
    }

    & $npmExecutable run build
    $npmBuildExitCode = $LASTEXITCODE
    if ($npmBuildExitCode -ne 0) {
        throw 'Angular production build failed.'
    }
}
finally {
    Pop-Location
}

Write-Host ''
Write-Host 'Company DLP Central Admin verification completed successfully.' -ForegroundColor Green
