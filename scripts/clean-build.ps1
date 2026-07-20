$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Get-ChildItem -Path $root -Directory -Recurse -Force |
    Where-Object { $_.Name -in @('bin', 'obj') } |
    Sort-Object FullName -Descending |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

dotnet restore
if ($LASTEXITCODE -ne 0) { throw "Restore failed." }

dotnet build
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

Write-Host "Clean build succeeded." -ForegroundColor Green
