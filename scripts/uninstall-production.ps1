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

[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $null, "Machine")
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Company DLP was removed and the installer-owned browser policy values were restored." -ForegroundColor Green
Write-Host "Audit and policy files remain under $env:ProgramData\CompanyDlp for investigation. Delete them manually only after approval." -ForegroundColor Yellow
