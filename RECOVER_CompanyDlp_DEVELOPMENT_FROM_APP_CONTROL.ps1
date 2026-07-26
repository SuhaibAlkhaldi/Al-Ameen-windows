param(
    [string]$ProjectRoot = ".",
    [switch]$Build
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$launcher = Join-Path $root "scripts\run-development.ps1"
$apphostBackup = "$launcher.before-service-apphost-hotfix.bak"

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "run-development.ps1 was not found: $launcher"
}

Write-Host "[1/5] Restoring the Development launcher to the signed dotnet.exe hosting path." -ForegroundColor Cyan

if (Test-Path -LiteralPath $apphostBackup) {
    Copy-Item -LiteralPath $apphostBackup -Destination $launcher -Force
    Write-Host "Restored launcher from: $apphostBackup" -ForegroundColor Green
}
else {
    $text = [System.IO.File]::ReadAllText($launcher)
    $normalized = $text -replace "`r`n", "`n"

    $normalized = $normalized.Replace(
        '$serviceExe = Join-Path $root "src\CompanyDlp.Service\bin\Debug\net8.0-windows\CompanyDlp.Service.exe"',
        '$serviceDll = Join-Path $root "src\CompanyDlp.Service\bin\Debug\net8.0-windows\CompanyDlp.Service.dll"'
    )

    $normalized = $normalized.Replace(
        'foreach ($requiredFile in @($desktopExe, $serviceExe, $policy, $dotnetExe)) {',
        'foreach ($requiredFile in @($desktopExe, $serviceDll, $policy, $dotnetExe)) {'
    )

    $oldStart = @'
        Write-LauncherStep "Starting Company DLP Service through its Windows apphost executable."
        $service = Start-Process -FilePath $serviceExe `
            -WorkingDirectory (Split-Path -Parent $serviceExe) `
            -RedirectStandardOutput $serviceOut `
            -RedirectStandardError $serviceErr `
            -PassThru
'@

    $newStart = @'
        Write-LauncherStep "Starting Company DLP Service through signed dotnet.exe."
        $service = Start-Process -FilePath $dotnetExe `
            -ArgumentList ('"{0}"' -f $serviceDll) `
            -WorkingDirectory $root `
            -RedirectStandardOutput $serviceOut `
            -RedirectStandardError $serviceErr `
            -PassThru
'@

    if ($normalized.Contains($oldStart)) {
        $normalized = $normalized.Replace($oldStart, $newStart)
    }

    if (-not $normalized.Contains('Starting Company DLP Service through signed dotnet.exe.')) {
        throw "Could not safely restore the Service startup block. Backup the project and restore run-development.ps1 from the original source."
    }

    $normalized = $normalized -replace "(?<!`r)`n", "`r`n"
    [System.IO.File]::WriteAllText(
        $launcher,
        $normalized,
        (New-Object System.Text.UTF8Encoding($false))
    )
    Write-Host "Reversed the Service apphost modification." -ForegroundColor Green
}

$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $launcher,
    [ref]$tokens,
    [ref]$parseErrors
)

if ($parseErrors -and $parseErrors.Count -gt 0) {
    $messages = ($parseErrors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) -join "`r`n"
    throw "run-development.ps1 failed parser validation:`r`n$messages"
}

Write-Host "[2/5] Removing Mark-of-the-Web from the developer-owned project tree." -ForegroundColor Cyan
Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
    Unblock-File -ErrorAction SilentlyContinue
Write-Host "Project files unblocked." -ForegroundColor Green

Write-Host "[3/5] Removing old bin/obj output." -ForegroundColor Cyan
Get-ChildItem -LiteralPath $root -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("bin", "obj") } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Old build output removed." -ForegroundColor Green

Write-Host "[4/5] Verifying launcher configuration." -ForegroundColor Cyan
$launcherText = [System.IO.File]::ReadAllText($launcher)
if ($launcherText -notmatch 'CompanyDlp\.Service\.dll') {
    throw "The launcher does not reference CompanyDlp.Service.dll."
}
if ($launcherText -notmatch 'Starting Company DLP Service through signed dotnet\.exe') {
    throw "The launcher is not configured to host the Service through dotnet.exe."
}
Write-Host "PowerShell parser validation: PASSED" -ForegroundColor Green
Write-Host "Service hosting mode: signed dotnet.exe + CompanyDlp.Service.dll" -ForegroundColor Green

if ($Build) {
    Write-Host "[5/5] Building CompanyDlp.sln in Debug configuration." -ForegroundColor Cyan
    Push-Location $root
    try {
        & dotnet build .\CompanyDlp.sln --configuration Debug
        $buildExitCode = $LASTEXITCODE
        if ($buildExitCode -ne 0) {
            throw "dotnet build failed with exit code $buildExitCode."
        }
    }
    finally {
        Pop-Location
    }

    # Unblock generated output as an additional development safeguard.
    Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\(bin|obj)\\' } |
        Unblock-File -ErrorAction SilentlyContinue

    Write-Host "Debug build succeeded and generated output was unblocked." -ForegroundColor Green
}
else {
    Write-Host "[5/5] Build skipped. Re-run with -Build to build now." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Recovery completed." -ForegroundColor Green
Write-Host "Next command: .\START_DEVELOPMENT.bat" -ForegroundColor Cyan
Write-Host "If Code Integrity event 3077 still blocks CompanyDlp.Service.dll, the active App Control policy must trust a signing certificate, catalog/hash rule, or supplemental policy for this Development build." -ForegroundColor Yellow
