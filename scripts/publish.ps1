param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    # Optional: when supplied, also produce a signed .crx and a self-hosted update.xml for the
    # Chrome/Edge extension (see scripts/pack-browser-extension.ps1 and
    # scripts/register-browser-force-install.ps1). Omit these to keep publish.ps1's previous
    # behavior (unpacked extension folder copy only).
    [string]$ExtensionPrivateKeyPath,
    [string]$ExtensionUpdateBaseUrl
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

if ($ExtensionPrivateKeyPath) {
    # Pack browser-extension FIRST so the copy below picks up the version bump this run produced.
    if (-not $ExtensionUpdateBaseUrl) { throw "-ExtensionUpdateBaseUrl is required together with -ExtensionPrivateKeyPath." }
    $packed = & (Join-Path $PSScriptRoot "pack-browser-extension.ps1") -PrivateKeyPath $ExtensionPrivateKeyPath
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "pack-browser-extension.ps1 failed." }
}

Copy-Item (Join-Path $root "browser-extension") (Join-Path $out "browser-extension") -Recurse -Force
Copy-Item (Join-Path $root "firefox-extension") (Join-Path $out "firefox-extension") -Recurse -Force
Copy-Item (Join-Path $root "config\policy.production.sample.json") (Join-Path $out "policy.production.sample.json") -Force

if ($ExtensionPrivateKeyPath) {
    $extensionsOut = Join-Path $out "extensions"
    New-Item $extensionsOut -ItemType Directory -Force | Out-Null
    Copy-Item $packed.CrxPath (Join-Path $extensionsOut "company-dlp.crx") -Force

    $codebase = "$($ExtensionUpdateBaseUrl.TrimEnd('/'))/extensions/company-dlp.crx"
    $updateXml = @"
<?xml version="1.0" encoding="UTF-8"?>
<gupdate xmlns="http://www.google.com/update2/response" protocol="2.0">
  <app appid="$($packed.ExtensionId)">
    <updatecheck codebase="$codebase" version="$($packed.Version)" />
  </app>
</gupdate>
"@
    [System.IO.File]::WriteAllText((Join-Path $extensionsOut "update.xml"), $updateXml, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Extension package + update manifest written to $extensionsOut" -ForegroundColor Green
    Write-Host "  Extension ID:  $($packed.ExtensionId)" -ForegroundColor Green
    Write-Host "  Version:       $($packed.Version)" -ForegroundColor Green
    Write-Host "  Update URL:    $ExtensionUpdateBaseUrl/extensions/update.xml" -ForegroundColor Green
    Write-Host "  Deploy step:   copy $extensionsOut\* to wherever `$ExtensionUpdateBaseUrl serves /extensions/ (e.g. the backend's wwwroot/extensions/)." -ForegroundColor Yellow
}

Write-Host "Published to $out" -ForegroundColor Green
