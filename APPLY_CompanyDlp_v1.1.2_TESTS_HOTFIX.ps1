[CmdletBinding()]
param(
    [string]$ProjectRoot = (Get-Location).Path,
    [switch]$Build,
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$testFile = Join-Path $ProjectRoot 'tests\CompanyDlp.Tests\CentralAdministrationTests.cs'
$solution = Join-Path $ProjectRoot 'CompanyDlp.sln'
$verifyScript = Join-Path $ProjectRoot 'scripts\verify-central-admin.ps1'

if (-not (Test-Path -LiteralPath $testFile)) {
    throw "Expected test source file was not found: $testFile"
}

$text = Get-Content -LiteralPath $testFile -Raw
if ($text -match '(?m)^using Xunit;\s*$') {
    Write-Host 'CentralAdministrationTests.cs is already patched.' -ForegroundColor Yellow
}
else {
    $marker = 'using Microsoft.Extensions.Options;'
    if (-not $text.Contains($marker)) {
        throw 'Expected insertion point was not found. The file may differ from the supported source version.'
    }

    $backup = "$testFile.v1.1.1.bak"
    if (-not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $testFile -Destination $backup
    }

    $patched = $text.Replace($marker, "$marker`r`nusing Xunit;")
    Set-Content -LiteralPath $testFile -Value $patched -Encoding UTF8
    Write-Host 'Patched CentralAdministrationTests.cs' -ForegroundColor Green
}

Write-Host 'Company DLP v1.1.2 tests hotfix applied.' -ForegroundColor Cyan

if ($Verify) {
    if (-not (Test-Path -LiteralPath $verifyScript)) {
        throw "Verification script was not found: $verifyScript"
    }

    & powershell -ExecutionPolicy Bypass -File $verifyScript -Configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Central Admin verification failed.' }
}
elif ($Build) {
    if (-not (Test-Path -LiteralPath $solution)) {
        throw "Solution file was not found: $solution"
    }

    & dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

    & dotnet test (Join-Path $ProjectRoot 'tests\CompanyDlp.Tests\CompanyDlp.Tests.csproj') --configuration Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'dotnet test failed.' }
}
