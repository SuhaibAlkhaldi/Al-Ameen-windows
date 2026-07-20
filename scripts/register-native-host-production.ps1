#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory=$true)] [string]$NativeHostExe,
    [string[]]$ExtensionIds,
    [string[]]$FirefoxExtensionIds = @()
)
$ErrorActionPreference = "Stop"
if (-not $ExtensionIds -or $ExtensionIds.Count -eq 0) { throw "At least one published Chrome/Edge extension ID is required." }

$installDir = Split-Path $NativeHostExe -Parent
$manifestDir = Split-Path $installDir -Parent

$origins = $ExtensionIds | Where-Object { $_ -and $_ -notlike "REPLACE*" } | ForEach-Object { "chrome-extension://$_/" }
if (-not $origins) { throw "Valid published Chrome/Edge extension IDs are required." }

$chromiumManifestPath = Join-Path $manifestDir "com.company.dlp.chromium.json"
$chromiumManifest = @{
    name = "com.company.dlp"
    description = "Company DLP native messaging host"
    path = $NativeHostExe
    type = "stdio"
    allowed_origins = @($origins)
} | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($chromiumManifestPath, $chromiumManifest, (New-Object System.Text.UTF8Encoding($false)))

foreach ($path in @(
    "HKLM:\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.company.dlp",
    "HKLM:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.company.dlp"
)) {
    New-Item $path -Force | Out-Null
    Set-Item $path -Value $chromiumManifestPath
}

$validFirefoxIds = @($FirefoxExtensionIds | Where-Object { $_ -and $_ -notlike "REPLACE*" })
if ($validFirefoxIds.Count -gt 0) {
    $firefoxManifestPath = Join-Path $manifestDir "com.company.dlp.firefox.json"
    $firefoxManifest = @{
        name = "com.company.dlp"
        description = "Company DLP native messaging host for Firefox"
        path = $NativeHostExe
        type = "stdio"
        allowed_extensions = $validFirefoxIds
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($firefoxManifestPath, $firefoxManifest, (New-Object System.Text.UTF8Encoding($false)))

    $firefoxRegistryPath = "HKLM:\SOFTWARE\Mozilla\NativeMessagingHosts\com.company.dlp"
    New-Item $firefoxRegistryPath -Force | Out-Null
    Set-Item $firefoxRegistryPath -Value $firefoxManifestPath
}
