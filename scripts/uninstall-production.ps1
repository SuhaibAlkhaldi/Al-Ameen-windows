#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
$installDir = Join-Path $env:ProgramFiles "CompanyDlp"
Get-Process -Name @("CompanyDlp.Desktop", "CompanyDlp.NativeHost") -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue
sc.exe delete CompanyDlp | Out-Null
Start-Sleep -Seconds 1
& (Join-Path $PSScriptRoot "unregister-production-context-menu.ps1")

$shellExtensionRegisterExe = Join-Path $installDir "ShellExtension\CompanyDlp.ShellExtension.Register.exe"
if (Test-Path $shellExtensionRegisterExe) {
    & (Join-Path $PSScriptRoot "unregister-shell-extension.ps1") -RegisterExe $shellExtensionRegisterExe
}

Remove-Item "HKLM:\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue


# Two independent mechanisms can write these list policies with different value names:
# register-browser-force-install.ps1 (a numeric list index, "1") and BrowserPolicyManager.cs's
# runtime-applied policy (fixed literal "9999"). Clean up both regardless of which one actually ran.
foreach ($item in @(
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"; Name = "1" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"; Name = "1" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallBlocklist"; Name = "1" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallBlocklist"; Name = "1" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"; Name = "9999" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"; Name = "9999" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallBlocklist"; Name = "9999" },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallBlocklist"; Name = "9999" }
)) {
    Remove-ItemProperty $item.Path -Name $item.Name -Force -ErrorAction SilentlyContinue
}

& (Join-Path $PSScriptRoot "production-browser-policy-backup.ps1") -Mode Restore

# Reset (not merge) the AppLocker Exe rule collection to NotConfigured/empty. CliExecutionPolicyManager
# only ever writes via Set-AppLockerPolicy -Merge, which can add/update rules by fixed Id but can never
# remove one, so this is the one supported way to actually undo its wt.exe Deny rule and the
# "Allow Everyone: *" safety-net rule it ships alongside it. Scoped to Type="Exe" only, so it does not
# touch any Msi/Script/Dll/Appx AppLocker rules an admin may have configured separately on this device.
$resetAppLockerXmlPath = Join-Path $env:TEMP "CompanyDlp-AppLocker-Reset-$([Guid]::NewGuid().ToString('N')).xml"
try {
    Set-Content -Path $resetAppLockerXmlPath -Encoding UTF8 -Value @'
<AppLockerPolicy Version="1">
  <RuleCollection Type="Exe" EnforcementMode="NotConfigured" />
</AppLockerPolicy>
'@
    if (Get-Command Set-AppLockerPolicy -ErrorAction SilentlyContinue) {
        Set-AppLockerPolicy -XmlPolicy $resetAppLockerXmlPath -ErrorAction SilentlyContinue
    }
} finally {
    Remove-Item $resetAppLockerXmlPath -Force -ErrorAction SilentlyContinue
}

# Explorer DisallowRun/RestrictRun (see CliExecutionPolicyManager.ApplyExplorerDisallowRun) is written
# directly into the *employee's own* HKEY_USERS hive while they are logged in, not into HKLM like every
# other policy this script cleans up - so it can persist in a profile that is not currently loaded (the
# employee is logged off, or a different account is running this uninstall) at the moment of uninstall.
# Walk every local profile's NTUSER.DAT, mounting it as a temporary hive when it is not already loaded
# under HKEY_USERS, so uninstall does not leave a stale cmd.exe/PowerShell block behind for whichever
# employee account this ran against.
$explorerPolicySubPath = "Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"
Get-ChildItem "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList" -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -match '^S-1-5-21-' } |
    ForEach-Object {
        $sid = $_.PSChildName
        $profileImagePath = (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).ProfileImagePath
        $alreadyLoaded = Test-Path "Registry::HKEY_USERS\$sid"
        $tempHiveName = "CompanyDlpUninstall_$sid"
        $hiveMounted = $false

        try {
            if ($alreadyLoaded) {
                $hiveRoot = "Registry::HKEY_USERS\$sid"
            } else {
                if (-not $profileImagePath) { return }
                $ntUserDatPath = Join-Path $profileImagePath "NTUSER.DAT"
                if (-not (Test-Path $ntUserDatPath)) { return }
                & reg.exe load "HKU\$tempHiveName" "$ntUserDatPath" | Out-Null
                if ($LASTEXITCODE -ne 0) { return }
                $hiveMounted = $true
                $hiveRoot = "Registry::HKEY_USERS\$tempHiveName"
            }

            $explorerKeyPath = Join-Path $hiveRoot $explorerPolicySubPath
            if (Test-Path $explorerKeyPath) {
                Remove-ItemProperty -Path $explorerKeyPath -Name "DisallowRun" -Force -ErrorAction SilentlyContinue
                Remove-Item -Path (Join-Path $explorerKeyPath "RestrictRun") -Recurse -Force -ErrorAction SilentlyContinue
            }
        } finally {
            if ($hiveMounted) {
                [gc]::Collect()
                [gc]::WaitForPendingFinalizers()
                & reg.exe unload "HKU\$tempHiveName" | Out-Null
            }
        }
    }

[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $null, "Machine")
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Company DLP was removed and the installer-owned browser policy values were restored." -ForegroundColor Green
Write-Host "Audit and policy files remain under $env:ProgramData\CompanyDlp for investigation. Delete them manually only after approval." -ForegroundColor Yellow
