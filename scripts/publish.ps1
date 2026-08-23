param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SelfContained,
    # Optional: when supplied, also produce a signed .crx and a self-hosted update.xml for the
    # Chrome/Edge extension (see scripts/pack-browser-extension.ps1 and
    # scripts/register-browser-force-install.ps1). Omit these to keep publish.ps1's previous
    # behavior (unpacked extension folder copy only).
    [string]$ExtensionPrivateKeyPath,
    [string]$ExtensionUpdateBaseUrl,
    # Optional, Firefox side of the same thing: an ALREADY Mozilla-signed .xpi (see
    # scripts/pack-firefox-extension.ps1 - signing is a one-time manual step against a real Mozilla
    # Developer Hub account, so publish.ps1 cannot produce this file itself the way it does the
    # Chrome/Edge .crx). Requires -ExtensionUpdateBaseUrl too (shared with Chrome/Edge above).
    [string]$FirefoxSignedXpiPath
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
New-Item $out -ItemType Directory -Force | Out-Null

if ($ExtensionPrivateKeyPath) {
    # Pack browser-extension FIRST so the copy below picks up the version bump this run produced.
    if (-not $ExtensionUpdateBaseUrl) { throw "-ExtensionUpdateBaseUrl is required together with -ExtensionPrivateKeyPath." }
    $packed = & (Join-Path $PSScriptRoot "pack-browser-extension.ps1") -PrivateKeyPath $ExtensionPrivateKeyPath
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "pack-browser-extension.ps1 failed." }
}

if ($FirefoxSignedXpiPath) {
    if (-not $ExtensionUpdateBaseUrl) { throw "-ExtensionUpdateBaseUrl is required together with -FirefoxSignedXpiPath." }
    if (-not (Test-Path $FirefoxSignedXpiPath)) { throw "Firefox signed .xpi was not found: $FirefoxSignedXpiPath" }
    $firefoxManifestPath = Join-Path $root "firefox-extension\manifest.json"
    $firefoxManifest = Get-Content $firefoxManifestPath -Raw | ConvertFrom-Json
    $firefoxExtensionId = $firefoxManifest.browser_specific_settings.gecko.id
    if (-not $firefoxExtensionId) { throw "firefox-extension/manifest.json is missing browser_specific_settings.gecko.id." }
}

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

# CompanyDlp.ShellExtension/.Register target classic .NET Framework 4.8 (not net8.0(-windows) like
# every other project here) - the in-proc COM InfoTip handler explorer.exe loads is far more
# reliable hosted from .NET Framework. A framework-dependent .NET Framework build doesn't take
# -r/--self-contained the way the net8.0 projects above do; it's just a plain configuration publish.
$netFrameworkProjects = @(
    @{ Name = "ShellExtension"; Path = "src\CompanyDlp.ShellExtension\CompanyDlp.ShellExtension.csproj" },
    @{ Name = "ShellExtension"; Path = "src\CompanyDlp.ShellExtension.Register\CompanyDlp.ShellExtension.Register.csproj" }
)
foreach ($project in $netFrameworkProjects) {
    dotnet publish (Join-Path $root $project.Path) -c $Configuration -o (Join-Path $out $project.Name)
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($project.Path)." }
}

Copy-Item (Join-Path $root "browser-extension") (Join-Path $out "browser-extension") -Recurse -Force
Copy-Item (Join-Path $root "firefox-extension") (Join-Path $out "firefox-extension") -Recurse -Force
Copy-Item (Join-Path $root "config\policy.production.sample.json") (Join-Path $out "policy.production.sample.json") -Force
Write-Host "Published to $out" -ForegroundColor Green

if ($ExtensionPrivateKeyPath -or $FirefoxSignedXpiPath) {
    $extensionsOut = Join-Path $out "extensions"
    New-Item $extensionsOut -ItemType Directory -Force | Out-Null
    $outputPolicyPath = Join-Path $out "policy.production.sample.json"
    $outputPolicy = Get-Content $outputPolicyPath -Raw | ConvertFrom-Json
}

if ($ExtensionPrivateKeyPath) {
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

    # Written directly into the published policy file (not just printed) so a real deployment can never
    # ship this policy with the Chrome/Edge extension fields still at their unfilled "REPLACE_..."
    # template value - see BrowserPolicyManager.ExtensionPolicyValidator for what happens if it does
    # (nothing gets force-installed, and - as of this fix - nothing gets blocked either, rather than the
    # old "everything blocked, nothing protected" failure mode).
    $updateXmlUrl = "$($ExtensionUpdateBaseUrl.TrimEnd('/'))/extensions/update.xml"
    $outputPolicy.browser.chromeExtensionId = $packed.ExtensionId
    $outputPolicy.browser.chromeExtensionUpdateUrl = $updateXmlUrl
    $outputPolicy.browser.edgeExtensionId = $packed.ExtensionId
    $outputPolicy.browser.edgeExtensionUpdateUrl = $updateXmlUrl

    Write-Host "Extension package + update manifest written to $extensionsOut" -ForegroundColor Green
    Write-Host "  Extension ID:  $($packed.ExtensionId)" -ForegroundColor Green
    Write-Host "  Version:       $($packed.Version)" -ForegroundColor Green
    Write-Host "  Update URL:    $updateXmlUrl" -ForegroundColor Green
    Write-Host "  Deploy step:   copy $extensionsOut\* to wherever `$ExtensionUpdateBaseUrl serves /extensions/ (e.g. the backend's wwwroot/extensions/)." -ForegroundColor Yellow
}

if ($FirefoxSignedXpiPath) {
    Copy-Item $FirefoxSignedXpiPath (Join-Path $extensionsOut "company-dlp.xpi") -Force
    $xpiUrl = "$($ExtensionUpdateBaseUrl.TrimEnd('/'))/extensions/company-dlp.xpi"

    $outputPolicy.browser | Add-Member -NotePropertyName "firefoxExtensionId" -NotePropertyValue $firefoxExtensionId -Force
    $outputPolicy.browser | Add-Member -NotePropertyName "firefoxExtensionUpdateUrl" -NotePropertyValue $xpiUrl -Force

    Write-Host "Firefox signed .xpi copied to $extensionsOut\company-dlp.xpi" -ForegroundColor Green
    Write-Host "  Extension ID:  $firefoxExtensionId" -ForegroundColor Green
    Write-Host "  Install URL:   $xpiUrl" -ForegroundColor Green
    Write-Host "  Deploy step:   copy $extensionsOut\company-dlp.xpi to wherever `$ExtensionUpdateBaseUrl serves /extensions/." -ForegroundColor Yellow
}

if ($ExtensionPrivateKeyPath -or $FirefoxSignedXpiPath) {
    $outputPolicy | ConvertTo-Json -Depth 20 | Set-Content $outputPolicyPath -Encoding UTF8
    Write-Host "Extension id/update URL values were written directly into $outputPolicyPath." -ForegroundColor Green
}
