param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $out -ItemType Directory -Force | Out-Null

$projects = @(
    @{ Name = "Service"; Path = "src\CompanyDlp.Service\CompanyDlp.Service.csproj" },
    @{ Name = "Desktop"; Path = "src\CompanyDlp.Desktop\CompanyDlp.Desktop.csproj" },
    @{ Name = "NativeHost"; Path = "src\CompanyDlp.NativeHost\CompanyDlp.NativeHost.csproj" }
)

foreach ($project in $projects) {
    $args = @("publish", (Join-Path $root $project.Path), "-c", $Configuration, "-r", $Runtime, "-o", (Join-Path $out $project.Name))
    if ($SelfContained) { $args += @("--self-contained", "true") } else { $args += @("--self-contained", "false") }
    dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($project.Name)." }
}

Copy-Item (Join-Path $root "browser-extension") (Join-Path $out "browser-extension") -Recurse -Force
Copy-Item (Join-Path $root "firefox-extension") (Join-Path $out "firefox-extension") -Recurse -Force
Copy-Item (Join-Path $root "config\policy.production.sample.json") (Join-Path $out "policy.production.sample.json") -Force
Write-Host "Published to $out" -ForegroundColor Green
