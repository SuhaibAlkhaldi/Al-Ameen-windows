[CmdletBinding()]
param(
    [string]$ProjectRoot = '.',
    [switch]$BuildAngular
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Utf8NoBom {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Content
    )

    $encoding = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $encoding)
}

$root = (Resolve-Path $ProjectRoot).Path
$portal = Join-Path $root 'src\CompanyDlp.AdminPortal'
$lockFile = Join-Path $portal 'package-lock.json'
$npmRcFile = Join-Path $portal '.npmrc'
$verifyScript = Join-Path $root 'scripts\verify-central-admin.ps1'

if (-not (Test-Path $portal)) {
    throw "Angular portal was not found: $portal"
}
if (-not (Test-Path $lockFile)) {
    throw "package-lock.json was not found: $lockFile"
}
if (-not (Test-Path $verifyScript)) {
    throw "Verification script was not found: $verifyScript"
}

$internalRegistry = 'https://packages.applied-caas-gateway1.internal.api.openai.org/artifactory/api/npm/npm-public/'
$publicRegistry = 'https://registry.npmjs.org/'

$lockContent = Get-Content -LiteralPath $lockFile -Raw
$occurrences = ([regex]::Matches($lockContent, [regex]::Escape($internalRegistry))).Count

if ($occurrences -gt 0) {
    $backupPath = "$lockFile.v1.1.3.bak"
    if (-not (Test-Path $backupPath)) {
        Copy-Item -LiteralPath $lockFile -Destination $backupPath
    }

    $lockContent = $lockContent.Replace($internalRegistry, $publicRegistry)
    Write-Utf8NoBom -Path $lockFile -Content $lockContent
    Write-Host "Replaced $occurrences internal registry URL(s) in package-lock.json." -ForegroundColor Green
}
else {
    Write-Host 'package-lock.json is already using a portable registry.' -ForegroundColor DarkGreen
}

$npmRc = "registry=https://registry.npmjs.org/`r`naudit=false`r`nfund=false`r`n"
[System.IO.File]::WriteAllText($npmRcFile, $npmRc, [System.Text.Encoding]::ASCII)
Write-Host 'Created project-level .npmrc for the public npm registry.' -ForegroundColor Green

$verifyContent = Get-Content -LiteralPath $verifyScript -Raw
$replacementBlock = @'
    $npmCiArgs = @('ci', '--registry=https://registry.npmjs.org/', '--no-audit', '--no-fund')
    & npm @npmCiArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'npm ci failed. Retrying a clean install with npm install as a workaround for the npm CLI exit-handler bug.'
        $nodeModules = Join-Path $portal 'node_modules'
        if (Test-Path $nodeModules) {
            Remove-Item -LiteralPath $nodeModules -Recurse -Force
        }

        $npmInstallArgs = @('install', '--registry=https://registry.npmjs.org/', '--no-audit', '--no-fund')
        & npm @npmInstallArgs
        if ($LASTEXITCODE -ne 0) { throw 'Angular dependency installation failed with both npm ci and npm install.' }
    }

    & npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Angular production build failed.' }
'@

if ($verifyContent.Contains('$npmCiArgs')) {
    Write-Host 'verify-central-admin.ps1 is already patched.' -ForegroundColor DarkGreen
}
else {
    $pattern = "(?ms)^    npm ci\r?\n    if \(\$LASTEXITCODE -ne 0\) \{ throw 'npm ci failed\.' \}\r?\n    npm run build\r?\n    if \(\$LASTEXITCODE -ne 0\) \{ throw 'Angular production build failed\.' \}"
    $patchedVerifyContent = [regex]::Replace($verifyContent, $pattern, $replacementBlock, 1)

    if ($patchedVerifyContent -eq $verifyContent) {
        throw 'Could not locate the expected npm block in verify-central-admin.ps1. No unsafe automatic edit was performed.'
    }

    $backupPath = "$verifyScript.v1.1.3.bak"
    if (-not (Test-Path $backupPath)) {
        Copy-Item -LiteralPath $verifyScript -Destination $backupPath
    }

    Write-Utf8NoBom -Path $verifyScript -Content $patchedVerifyContent
    Write-Host 'Patched verify-central-admin.ps1 with portable npm installation and fallback handling.' -ForegroundColor Green
}

Write-Host 'Company DLP v1.1.4 npm registry hotfix applied.' -ForegroundColor Cyan

if ($BuildAngular) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Required command 'node' was not found in PATH."
    }
    if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
        throw "Required command 'npm' was not found in PATH."
    }

    Push-Location $portal
    try {
        Write-Host "Node: $(node --version)" -ForegroundColor Cyan
        Write-Host "npm:  $(npm --version)" -ForegroundColor Cyan

        & npm ci --registry=https://registry.npmjs.org/ --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) {
            Write-Warning 'npm ci failed. Retrying with npm install.'
            if (Test-Path 'node_modules') {
                Remove-Item -LiteralPath 'node_modules' -Recurse -Force
            }
            & npm install --registry=https://registry.npmjs.org/ --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) {
                throw 'Angular dependency installation failed.'
            }
        }

        & npm run build
        if ($LASTEXITCODE -ne 0) {
            throw 'Angular production build failed.'
        }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Angular dependency installation and production build succeeded.' -ForegroundColor Green
}
