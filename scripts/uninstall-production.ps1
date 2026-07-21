#Requires -RunAsAdministrator
$ErrorActionPreference = "Continue"
Get-Process -Name @("CompanyDlp.Desktop", "CompanyDlp.NativeHost") -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Stop-Service CompanyDlp -Force -ErrorAction SilentlyContinue
sc.exe delete CompanyDlp | Out-Null
Start-Sleep -Seconds 1
& (Join-Path $PSScriptRoot "unregister-production-context-menu.ps1")

Remove-Item "HKLM:\SOFTWARE\Google\Chrome\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\SOFTWARE\Microsoft\Edge\NativeMessagingHosts\com.company.dlp" -Recurse -Force -ErrorAction SilentlyContinue


# Value name must match what register-browser-force-install.ps1 actually creates.
$browserExtensionValueName = "CompanyDlpBrowserExtension"
foreach ($item in @(
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallForcelist"; Name = $browserExtensionValueName },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"; Name = $browserExtensionValueName },
    @{ Path = "HKLM:\SOFTWARE\Policies\Google\Chrome\ExtensionInstallBlocklist"; Name = $browserExtensionValueName },
    @{ Path = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallBlocklist"; Name = $browserExtensionValueName }
)) {
    Remove-ItemProperty $item.Path -Name $item.Name -Force -ErrorAction SilentlyContinue
}

& (Join-Path $PSScriptRoot "production-browser-policy-backup.ps1") -Mode Restore

[Environment]::SetEnvironmentVariable("COMPANY_DLP_MODE", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_POLICY_PATH", $null, "Machine")
[Environment]::SetEnvironmentVariable("COMPANY_DLP_SESSION_AGENT_EXE", $null, "Machine")
Remove-Item (Join-Path $env:ProgramFiles "CompanyDlp") -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Company DLP was removed and the installer-owned browser policy values were restored." -ForegroundColor Green
Write-Host "Audit and policy files remain under $env:ProgramData\CompanyDlp for investigation. Delete them manually only after approval." -ForegroundColor Yellow
