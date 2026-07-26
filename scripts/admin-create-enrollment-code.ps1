[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5060",
    [Parameter(Mandatory = $true)] [string]$Email,
    [string]$Description = "Development workstation enrollment",
    [int]$ValidForMinutes = 30
)

$ErrorActionPreference = "Stop"
$securePassword = Read-Host "Admin password" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $login = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/auth/login" -ContentType "application/json" -Body (@{ email = $Email; password = $password } | ConvertTo-Json)
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $body = @{ description = $Description; validForMinutes = $ValidForMinutes } | ConvertTo-Json
    $result = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/enrollment-codes" -Headers $headers -ContentType "application/json" -Body $body
    Write-Host "Enrollment code expires at $($result.expiresAtUtc). It is shown only once:" -ForegroundColor Yellow
    Write-Host $result.enrollmentCode -ForegroundColor Green
    $result
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
}
