#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)] [string]$ChromeExtensionId,
    [Parameter(Mandatory = $true)] [string]$ChromeExtensionUpdateUrl,
    [Parameter(Mandatory = $true)] [string]$EdgeExtensionId,
    [Parameter(Mandatory = $true)] [string]$EdgeExtensionUpdateUrl,
    # Firefox is optional (unlike Chrome/Edge above) since it needs a Mozilla-signed .xpi (see
    # scripts/pack-firefox-extension.ps1) before it can be force-installed at all - omit both to skip
    # Firefox entirely and only register Chrome/Edge.
    [string]$FirefoxExtensionId,
    [string]$FirefoxExtensionXpiUrl
)
$ErrorActionPreference = "Stop"

# Chrome/Edge convert a list-type policy's registry subkey (ExtensionInstallForcelist/Blocklist) into
# a JSON array only when the subkey's value names are exactly "1".."N" for however many values exist
# (RegistryDict::ToValue() in Chromium's components/policy/core/common/policy_loader_win.cc) - any
# other naming makes Chromium treat the subkey as an object instead of a list, which fails
# ExtensionInstallForcelist's list-typed schema validation and gets silently discarded: the key/value
# round-trips fine through the registry, but Chrome never issues the extension update fetch at all (no
# request, no error, nothing). BrowserPolicyManager.cs (CompanyDlp.Service) uses this exact same value
# name now too - see its ExtensionPolicyValueName constant - single source of truth between the two.
# uninstall-production.ps1 removes this same name (and, for upgrade safety, the old "9999" this project
# used before that fix).
$valueName = "1"

function Set-ForceInstall {
    param([string]$PolicyRoot, [string]$ExtensionId, [string]$UpdateUrl)
    if ($ExtensionId -like "REPLACE*" -or $UpdateUrl -like "REPLACE*") {
        throw "${PolicyRoot}: extension id/update URL still looks like an unfilled placeholder ('$ExtensionId' / '$UpdateUrl')."
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

if ($FirefoxExtensionId -or $FirefoxExtensionXpiUrl) {
    if (-not $FirefoxExtensionId -or -not $FirefoxExtensionXpiUrl) {
        throw "Both -FirefoxExtensionId and -FirefoxExtensionXpiUrl are required together, or neither."
    }
    if ($FirefoxExtensionId -like "REPLACE*" -or $FirefoxExtensionXpiUrl -like "REPLACE*") {
        throw "Firefox: extension id/xpi URL still looks like an unfilled placeholder ('$FirefoxExtensionId' / '$FirefoxExtensionXpiUrl')."
    }

    # Firefox's ExtensionSettings registry mapping (documented by Mozilla's own policy-templates
    # project, mozilla/policy-templates) is one VALUE PER EXTENSION under this key - the value NAME is
    # the extension id itself (or "*" for the catch-all rule), and the value DATA is a JSON string for
    # just that one entry. Structurally different from Chrome/Edge's numbered-list-of-strings
    # convention above ($valueName does not apply to Firefox at all).
    # IMPORTANT: Firefox Release refuses to install an unsigned .xpi even via this enterprise policy -
    # $FirefoxExtensionXpiUrl must point at a build produced by scripts/pack-firefox-extension.ps1
    # AFTER it has been signed through Mozilla's Add-on signing (as "unlisted") - see that script for
    # the one-time manual account-setup step this depends on.
    # NOT independently confirmed live in this change against a real Firefox install - this session had
    # no elevated/administrator access to write HKLM policy keys and observe about:policies. Confirm on
    # a real admin-capable Windows+Firefox test machine before relying on this in production.
    $firefoxSettingsPath = "HKLM:\SOFTWARE\Policies\Mozilla\Firefox\ExtensionSettings"
    New-Item $firefoxSettingsPath -Force | Out-Null
    $forceInstallEntry = @{ installation_mode = "force_installed"; install_url = $FirefoxExtensionXpiUrl } | ConvertTo-Json -Compress
    New-ItemProperty $firefoxSettingsPath -Name $FirefoxExtensionId -Value $forceInstallEntry -PropertyType String -Force | Out-Null
    $blockedEntry = @{ installation_mode = "blocked" } | ConvertTo-Json -Compress
    New-ItemProperty $firefoxSettingsPath -Name "*" -Value $blockedEntry -PropertyType String -Force | Out-Null

    Write-Host "Firefox ExtensionSettings policy registered for $FirefoxExtensionId." -ForegroundColor Green
} else {
    Write-Host "Firefox extension parameters were not supplied - Firefox force-install/block was not configured." -ForegroundColor Yellow
}

Write-Host "Restart Chrome/Edge/Firefox (or reboot) for the force-install to take effect." -ForegroundColor Yellow
