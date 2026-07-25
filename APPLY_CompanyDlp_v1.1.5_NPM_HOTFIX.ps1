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

function Get-NativeExitCode {
    $exitCodeVariable = Get-Variable -Name LASTEXITCODE -ErrorAction SilentlyContinue
    if ($null -eq $exitCodeVariable) {
        return 1
    }

    return [int]$exitCodeVariable.Value
}

function Resolve-NpmCommand {
    $npmCommand = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -eq $npmCommand) {
        $npmCommand = Get-Command npm -ErrorAction SilentlyContinue
    }

    if ($null -eq $npmCommand) {
        throw "Required command 'npm' was not found in PATH."
    }

    if (-not [string]::IsNullOrWhiteSpace($npmCommand.Source)) {
        return $npmCommand.Source
    }

    return $npmCommand.Path
}

$root = (Resolve-Path $ProjectRoot).Path
$portal = Join-Path $root 'src\CompanyDlp.AdminPortal'
$lockFile = Join-Path $portal 'package-lock.json'
$npmRcFile = Join-Path $portal '.npmrc'
$verifyScript = Join-Path $root 'scripts\verify-central-admin.ps1'

if (-not (Test-Path -LiteralPath $portal)) {
    throw "Angular portal was not found: $portal"
}
if (-not (Test-Path -LiteralPath $lockFile)) {
    throw "package-lock.json was not found: $lockFile"
}
if (-not (Test-Path -LiteralPath $verifyScript)) {
    throw "Verification script was not found: $verifyScript"
}

$internalRegistry = 'https://packages.applied-caas-gateway1.internal.api.openai.org/artifactory/api/npm/npm-public/'
$publicRegistry = 'https://registry.npmjs.org/'

$lockContent = Get-Content -LiteralPath $lockFile -Raw
$occurrences = ([regex]::Matches($lockContent, [regex]::Escape($internalRegistry))).Count

if ($occurrences -gt 0) {
    $backupPath = "$lockFile.v1.1.4.bak"
    if (-not (Test-Path -LiteralPath $backupPath)) {
        Copy-Item -LiteralPath $lockFile -Destination $backupPath
    }

    $lockContent = $lockContent.Replace($internalRegistry, $publicRegistry)
    Write-Utf8NoBom -Path $lockFile -Content $lockContent
    Write-Host "Replaced $occurrences internal registry URL(s) in package-lock.json." -ForegroundColor Green
}
else {
    Write-Host 'package-lock.json is already using the public npm registry.' -ForegroundColor DarkGreen
}

$npmRc = "registry=https://registry.npmjs.org/`r`naudit=false`r`nfund=false`r`n"
[System.IO.File]::WriteAllText($npmRcFile, $npmRc, [System.Text.Encoding]::ASCII)
Write-Host 'Project-level .npmrc is configured for the public npm registry.' -ForegroundColor Green

$verifyContent = Get-Content -LiteralPath $verifyScript -Raw

$replacementBlock = @'
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
'@

$alreadyPatched = (
    $verifyContent.Contains('$npmCiExitCode = $LASTEXITCODE') -and
    $verifyContent.Contains('$npmExecutable run build')
)

if ($alreadyPatched) {
    Write-Host 'verify-central-admin.ps1 already contains the v1.1.5 npm handling.' -ForegroundColor DarkGreen
}
else {
    # Single-quoted here-string prevents PowerShell from trying to expand
    # $LASTEXITCODE while the regex pattern itself is being created.
    $pattern = @'
(?ms)^    (?:npm ci|\$npmCiArgs = @\('ci'.*?& npm @npmCiArgs).*?^    if \(\$LASTEXITCODE -ne 0\) \{ throw 'Angular production build failed\.' \}
?$
'@

    $regex = [regex]::new($pattern.Trim(), [System.Text.RegularExpressions.RegexOptions]::Multiline -bor [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $match = $regex.Match($verifyContent)

    if (-not $match.Success) {
        throw 'Could not locate the Angular npm verification block. No unsafe automatic edit was performed.'
    }

    $backupPath = "$verifyScript.v1.1.4.bak"
    if (-not (Test-Path -LiteralPath $backupPath)) {
        Copy-Item -LiteralPath $verifyScript -Destination $backupPath
    }

    $patchedVerifyContent = $verifyContent.Substring(0, $match.Index) +
                            $replacementBlock +
                            $verifyContent.Substring($match.Index + $match.Length)

    Write-Utf8NoBom -Path $verifyScript -Content $patchedVerifyContent
    Write-Host 'Patched verify-central-admin.ps1 with safe npm exit-code handling.' -ForegroundColor Green
}

Write-Host 'Company DLP v1.1.5 npm hotfix applied.' -ForegroundColor Cyan

if ($BuildAngular) {
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) {
        throw "Required command 'node' was not found in PATH."
    }

    $npmExecutable = Resolve-NpmCommand

    Push-Location $portal
    try {
        Write-Host 'Node version:' -ForegroundColor Cyan
        & node --version
        $nodeExitCode = Get-NativeExitCode
        if ($nodeExitCode -ne 0) {
            throw 'Unable to execute Node.js.'
        }

        Write-Host 'npm version:' -ForegroundColor Cyan
        & $npmExecutable --version
        $npmVersionExitCode = Get-NativeExitCode
        if ($npmVersionExitCode -ne 0) {
            throw 'Unable to execute npm.'
        }

        & $npmExecutable ci --registry=https://registry.npmjs.org/ --no-audit --no-fund
        $npmCiExitCode = Get-NativeExitCode

        if ($npmCiExitCode -ne 0) {
            Write-Warning 'npm ci failed. Retrying with a clean npm install.'
            if (Test-Path -LiteralPath 'node_modules') {
                Remove-Item -LiteralPath 'node_modules' -Recurse -Force
            }

            & $npmExecutable install --registry=https://registry.npmjs.org/ --no-audit --no-fund
            $npmInstallExitCode = Get-NativeExitCode
            if ($npmInstallExitCode -ne 0) {
                throw 'Angular dependency installation failed with both npm ci and npm install.'
            }
        }

        & $npmExecutable run build
        $npmBuildExitCode = Get-NativeExitCode
        if ($npmBuildExitCode -ne 0) {
            throw 'Angular production build failed.'
        }
    }
    finally {
        Pop-Location
    }

    Write-Host 'Angular dependency installation and production build succeeded.' -ForegroundColor Green
}
