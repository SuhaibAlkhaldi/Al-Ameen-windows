[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5060",
    [string]$TenantName = "Company DLP Development",
    [string]$AdminDisplayName = "DLP Administrator",
    [Parameter(Mandatory = $true)]
    [string]$Email
)

$ErrorActionPreference = "Stop"
$securePassword = Read-Host "Admin password (minimum 12 characters)" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $body = @{
        tenantName = $TenantName
        adminDisplayName = $AdminDisplayName
        email = $Email
        password = $password
    } | ConvertTo-Json
    $result = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/onboarding/register" -ContentType "application/json" -Body $body
    Write-Host "Tenant created: $($result.tenantId)" -ForegroundColor Green
    Write-Host "Admin user created: $($result.adminUserId)" -ForegroundColor Green
    Write-Host "Save the access token only for this PowerShell session; do not put it in source control." -ForegroundColor Yellow
    $result
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
}
