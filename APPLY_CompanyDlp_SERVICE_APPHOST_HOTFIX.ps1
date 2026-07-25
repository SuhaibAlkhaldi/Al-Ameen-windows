param(
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$launcherPath = Join-Path $root "scripts\run-development.ps1"

if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "run-development.ps1 was not found: $launcherPath"
}

$original = [System.IO.File]::ReadAllText($launcherPath)
$updated = $original -replace "`r`n", "`n"

$oldDeclaration = '$serviceDll = Join-Path $root "src\CompanyDlp.Service\bin\Debug\net8.0-windows\CompanyDlp.Service.dll"'
$newDeclaration = '$serviceExe = Join-Path $root "src\CompanyDlp.Service\bin\Debug\net8.0-windows\CompanyDlp.Service.exe"'

if ($updated.Contains($oldDeclaration)) {
    $updated = $updated.Replace($oldDeclaration, $newDeclaration)
}
elseif (-not $updated.Contains($newDeclaration)) {
    throw "Could not locate the Company DLP Service runtime declaration in run-development.ps1."
}

$oldRequired = 'foreach ($requiredFile in @($desktopExe, $serviceDll, $policy, $dotnetExe)) {'
$newRequired = 'foreach ($requiredFile in @($desktopExe, $serviceExe, $policy, $dotnetExe)) {'

if ($updated.Contains($oldRequired)) {
    $updated = $updated.Replace($oldRequired, $newRequired)
}
elseif (-not $updated.Contains($newRequired)) {
    throw "Could not locate the required runtime files list in run-development.ps1."
}

$oldStart = @'
        Write-LauncherStep "Starting Company DLP Service through signed dotnet.exe."
        $service = Start-Process -FilePath $dotnetExe `
            -ArgumentList ('"{0}"' -f $serviceDll) `
            -WorkingDirectory $root `
            -RedirectStandardOutput $serviceOut `
            -RedirectStandardError $serviceErr `
            -PassThru
'@

$newStart = @'
        Write-LauncherStep "Starting Company DLP Service through its Windows apphost executable."
        $service = Start-Process -FilePath $serviceExe `
            -WorkingDirectory (Split-Path -Parent $serviceExe) `
            -RedirectStandardOutput $serviceOut `
            -RedirectStandardError $serviceErr `
            -PassThru
'@

if ($updated.Contains($oldStart)) {
    $updated = $updated.Replace($oldStart, $newStart)
}
elseif (-not $updated.Contains($newStart)) {
    throw "Could not locate the Company DLP Service Start-Process block in run-development.ps1."
}

$updated = $updated -replace "(?<!`r)`n", "`r`n"

$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseInput(
    $updated,
    [ref]$tokens,
    [ref]$parseErrors
)

if ($parseErrors -and $parseErrors.Count -gt 0) {
    $messages = ($parseErrors | ForEach-Object { $_.Message }) -join "`r`n"
    throw "The patched launcher did not pass PowerShell parser validation:`r`n$messages"
}

if ($updated -eq $original) {
    Write-Host "Company DLP Service apphost hotfix is already applied." -ForegroundColor Yellow
    Write-Host "PowerShell parser validation: PASSED" -ForegroundColor Green
    exit 0
}

$backupPath = "$launcherPath.before-service-apphost-hotfix.bak"
if (-not (Test-Path -LiteralPath $backupPath)) {
    [System.IO.File]::WriteAllText(
        $backupPath,
        $original,
        (New-Object System.Text.UTF8Encoding($false))
    )
}

[System.IO.File]::WriteAllText(
    $launcherPath,
    $updated,
    (New-Object System.Text.UTF8Encoding($false))
)

Write-Host "Company DLP Development Service apphost hotfix applied." -ForegroundColor Green
Write-Host "PowerShell parser validation: PASSED" -ForegroundColor Green
Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
Write-Host "The launcher will now start CompanyDlp.Service.exe directly." -ForegroundColor Cyan
