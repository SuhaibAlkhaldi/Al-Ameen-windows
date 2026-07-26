[CmdletBinding()]
param(
    [string]$BaseUrl = "http://127.0.0.1:5060",
    [Parameter(Mandatory = $true)] [string]$Email,
    [Parameter(Mandatory = $true)]
    [ValidateSet(
        "agent.session", "screen.capture", "screen.recording", "clipboard.copy-sensitive",
        "browser.upload", "browser.drag-drop", "browser.file-paste", "browser.image-paste",
        "usb.device-connect", "usb.storage", "usb.mobile-device",
        "software.install", "software.execute-unapproved", "file.encrypt", "file.decrypt")]
    [string]$ActionKey,
    [Parameter(Mandatory = $true)] [bool]$Allowed,
    [ValidateSet("Global", "Employee", "Device", "Department", "UserSid", "Username", "MachineName")]
    [string]$ScopeType = "Global",
    [string]$ScopeId = "*",
    [int]$ExpiresInMinutes = 0,
    [string]$Reason = "Administrator policy change",
    [switch]$EmergencyDeny
)

$ErrorActionPreference = "Stop"
$securePassword = Read-Host "Admin password" -AsSecureString
$pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $password = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    $loginBody = @{ email = $Email; password = $password } | ConvertTo-Json
    $login = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/auth/login" -ContentType "application/json" -Body $loginBody
    $headers = @{ Authorization = "Bearer $($login.accessToken)" }
    $grant = @{
        actionKey = $ActionKey
        allowed = $Allowed
        scopeType = $ScopeType
        scopeId = $ScopeId
        priority = 100
        reason = $Reason
        emergencyDeny = [bool]$EmergencyDeny
    }
    if ($ExpiresInMinutes -gt 0) {
        $grant.expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes($ExpiresInMinutes).ToString("O")
    }
    $result = Invoke-RestMethod -Method Post -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/admin/permissions" -Headers $headers -ContentType "application/json" -Body ($grant | ConvertTo-Json)
    Write-Host "Permission saved. GrantId=$($result.id), Action=$($result.actionKey), Allowed=$($result.allowed)" -ForegroundColor Green
    $result
}
finally {
    if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $password = $null
}
