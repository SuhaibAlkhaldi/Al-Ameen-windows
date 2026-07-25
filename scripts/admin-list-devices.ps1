[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5060",
    [Parameter(Mandatory = $true)] [string]$Email
)

$ErrorActionPreference = "Stop"
$securePassword = Read-Host "Admin password" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $login = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/auth/login" -ContentType "application/json" -Body (@{ email = $Email; password = $password } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $devices = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/devices" -Headers $headers
    $devices | Format-Table id, machineName, employeeName, isActive, lastSeenAtUtc, lastAppliedPolicyVersion -AutoSize
    $devices
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
}
