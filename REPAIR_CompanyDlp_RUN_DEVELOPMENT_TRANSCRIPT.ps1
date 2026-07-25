param(
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"

$root = (Resolve-Path -LiteralPath $ProjectRoot).Path
$target = Join-Path $root "scripts\run-development.ps1"
$previousBackup = "$target.before-transcript-hotfix.bak"
$corruptBackup = "$target.corrupt.$(Get-Date -Format 'yyyyMMdd-HHmmss').bak"

if (-not (Test-Path -LiteralPath $target)) {
    throw "run-development.ps1 was not found at: $target"
}

Copy-Item -LiteralPath $target -Destination $corruptBackup -Force
Write-Host "Saved the current broken launcher to: $corruptBackup" -ForegroundColor DarkGray

if (Test-Path -LiteralPath $previousBackup) {
    $sourceText = [System.IO.File]::ReadAllText($previousBackup)
    Write-Host "Restoring from the pre-hotfix backup." -ForegroundColor Cyan
}
else {
    $sourceText = [System.IO.File]::ReadAllText($target)
    Write-Host "No pre-hotfix backup was found; repairing the current file." -ForegroundColor Yellow
}

# Normalize line endings only while locating/replacing the startup block.
$normalized = $sourceText -replace "`r`n", "`n"

$oldBlock = @'
try {
    Start-Transcript -Path (Join-Path $logRoot "launcher.transcript.log") -Force | Out-Null
    $transcriptStarted = $true
'@

$newBlock = @'
try {
    # Transcript logging is optional and must never prevent Development startup.
    # A unique file name avoids collisions with a stale or locked transcript.
    try {
        $transcriptPath = Join-Path $logRoot ("launcher.transcript.{0:yyyyMMdd-HHmmss-fff}.log" -f (Get-Date))
        Start-Transcript -Path $transcriptPath -Force -ErrorAction Stop | Out-Null
        $transcriptStarted = $true
    }
    catch {
        $transcriptStarted = $false
        $warning = "[{0:yyyy-MM-dd HH:mm:ss.fff}] Transcript logging was skipped: {1}" -f (Get-Date), $_.Exception.Message
        Write-Warning $warning
        Add-Content -LiteralPath $launcherLog -Value $warning -Encoding UTF8
    }
'@

if ($normalized.Contains($oldBlock)) {
    $repaired = $normalized.Replace($oldBlock, $newBlock)
}
elseif ($normalized.Contains("Transcript logging is optional and must never prevent Development startup.")) {
    # The target may already contain a valid repair. Use it as-is and only validate below.
    $repaired = $normalized
}
else {
    throw "Could not find the original transcript startup block. The broken launcher was preserved at: $corruptBackup"
}

# Write as UTF-8 without BOM and with Windows line endings.
$repaired = $repaired -replace "(?<!`r)`n", "`r`n"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($target, $repaired, $utf8NoBom)

# Parse the PowerShell file before considering the repair successful.
$tokens = $null
$parseErrors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    $target,
    [ref]$tokens,
    [ref]$parseErrors
)

if ($parseErrors -and $parseErrors.Count -gt 0) {
    Copy-Item -LiteralPath $corruptBackup -Destination $target -Force
    $messages = ($parseErrors | ForEach-Object { $_.Message }) -join "`r`n"
    throw "The repaired launcher did not pass PowerShell parsing, so the previous file was restored.`r`n$messages"
}

Write-Host "Company DLP Development launcher repaired successfully." -ForegroundColor Green
Write-Host "PowerShell parser validation: PASSED" -ForegroundColor Green
Write-Host "No .NET rebuild is required." -ForegroundColor Cyan
