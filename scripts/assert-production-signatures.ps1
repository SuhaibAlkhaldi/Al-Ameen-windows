#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot
)

$ErrorActionPreference = "Stop"
$files = Get-ChildItem $PublishRoot -Recurse -File | Where-Object { $_.Extension -in @(".exe", ".dll") }
if (-not $files) { throw "No production binaries were found under $PublishRoot." }

$invalid = foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature $file.FullName
    if ($signature.Status -ne "Valid") {
        [PSCustomObject]@{
            File = $file.FullName
            Status = $signature.Status
            Message = $signature.StatusMessage
        }
    }
}

if ($invalid) {
    $invalid | Format-Table -AutoSize -Wrap
    throw "Production installation stopped: every EXE and DLL must have a valid trusted Authenticode signature."
}

Write-Host "All production EXE and DLL files have valid Authenticode signatures." -ForegroundColor Green
