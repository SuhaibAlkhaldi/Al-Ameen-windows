#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)] [string]$ChromeExtensionId,
    [Parameter(Mandatory = $true)] [string]$ChromeExtensionUpdateUrl,
    [Parameter(Mandatory = $true)] [string]$EdgeExtensionId,
    [Parameter(Mandatory = $true)] [string]$EdgeExtensionUpdateUrl
)
$ErrorActionPreference = "Stop"

# Real, meaningful value name (not the placeholder "9999" uninstall-production.ps1 used to expect,
# which nothing here ever created). uninstall-production.ps1 removes this same name.
$valueName = "CompanyDlpBrowserExtension"

function Set-ForceInstall {
    param([string]$PolicyRoot, [string]$ExtensionId, [string]$UpdateUrl)
    if ($ExtensionId -like "REPLACE*" -or $UpdateUrl -like "REPLACE*") {
        throw "$PolicyRoot: extension id/update URL still looks like an unfilled placeholder ('$ExtensionId' / '$UpdateUrl')."
    }

    $forcelistPath = "$PolicyRoot\ExtensionInstallForcelist"
    New-Item $forcelistPath -Force | Out-Null
    New-ItemProperty $forcelistPath -Name $valueName -Value "$ExtensionId;$UpdateUrl" -PropertyType String -Force | Out-Null

    # blockUnapprovedExtensions in policy.production.sample.json implies only the force-installed
    # extension should ever be allowed to run. ExtensionInstallForcelist always supersedes
    # ExtensionInstallBlocklist for entries it lists (confirmed against current Chrome Enterprise
    # policy docs: https://chromeenterprise.google/policies/extension-install-forcelist/), so
    # blocking "*" here does not also block the extension we just force-installed.
    $blocklistPath = "$PolicyRoot\ExtensionInstallBlocklist"
    New-Item $blocklistPath -Force | Out-Null
    New-ItemProperty $blocklistPath -Name $valueName -Value "*" -PropertyType String -Force | Out-Null
}

# Force-installed extensions cannot be disabled or removed by the end user through
# chrome://extensions / edge://extensions purely as a side effect of ExtensionInstallForcelist —
# this is documented, built-in behavior (Chrome Enterprise: "install silently, without user
# interaction, and which users can't uninstall or turn off"; https://chromeenterprise.google/policies/extension-install-forcelist/).
# A separate ExtensionSettings entry is not required for that guarantee and is deliberately not
# added here, since it would need its own cleanup path in uninstall-production.ps1 for no added
# lock-down benefit over what Forcelist already provides.

Set-ForceInstall -PolicyRoot "HKLM:\SOFTWARE\Policies\Google\Chrome" -ExtensionId $ChromeExtensionId -UpdateUrl $ChromeExtensionUpdateUrl
Set-ForceInstall -PolicyRoot "HKLM:\SOFTWARE\Policies\Microsoft\Edge" -ExtensionId $EdgeExtensionId -UpdateUrl $EdgeExtensionUpdateUrl

Write-Host "Chrome and Edge ExtensionInstallForcelist/ExtensionInstallBlocklist policies registered (value name: $valueName)." -ForegroundColor Green
Write-Host "Restart Chrome/Edge (or reboot) for the force-install to take effect." -ForegroundColor Yellow
