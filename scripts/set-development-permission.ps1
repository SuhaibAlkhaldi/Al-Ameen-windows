param(
    [string]$ActionKey,
    [int]$Minutes,
    [ValidateSet("Allow", "Deny")]
    [string]$Decision = "Allow",
    [string]$Reason = "Approved for local development testing"
)

$ErrorActionPreference = "Stop"
$actions = @(
    "screen.capture",
    "screen.recording",
    "clipboard.copy-sensitive",
    "browser.upload",
    "browser.drag-drop",
    "browser.file-paste",
    "browser.image-paste",
    "usb.device-connect",
    "software.install",
    "software.execute-unapproved",
    "file.encrypt",
    "file.decrypt"
)

if ([string]::IsNullOrWhiteSpace($ActionKey)) {
    Write-Host "Available actions:" -ForegroundColor Cyan
    for ($index = 0; $index -lt $actions.Count; $index++) {
        Write-Host ("[{0}] {1}" -f ($index + 1), $actions[$index])
    }
    $selection = [int](Read-Host "Choose action number")
    if ($selection -lt 1 -or $selection -gt $actions.Count) { throw "Invalid action selection." }
    $ActionKey = $actions[$selection - 1]
}

if ($actions -notcontains $ActionKey) { throw "Unsupported action key: $ActionKey" }
if ($Minutes -le 0) { $Minutes = [int](Read-Host "Permission duration in minutes (1-1440)") }
$Minutes = [Math]::Min(1440, [Math]::Max(1, $Minutes))

try {
    $null = Invoke-RestMethod -Uri "http://127.0.0.1:5055/health" -TimeoutSec 2
} catch {
    throw "Company DLP Development Mock Server is not running. Start START_DEVELOPMENT.bat first."
}

$currentIdentity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$now = [DateTimeOffset]::UtcNow
$grant = @{
    grantId = [Guid]::NewGuid()
    actionKey = $ActionKey
    allowed = ($Decision -eq "Allow")
    subjectType = "UserSid"
    subjectId = $currentIdentity.User.Value
    source = "TemporaryGrant"
    priority = 500
    startsAtUtc = $now
    expiresAtUtc = $now.AddMinutes($Minutes)
    reason = $Reason
    grantedBy = "DevelopmentTestScript"
    createdAtUtc = $now
} | ConvertTo-Json -Depth 6

$result = Invoke-RestMethod `
    -Method Post `
    -Uri "http://127.0.0.1:5055/api/v1/development/temporary-permissions" `
    -ContentType "application/json" `
    -Body $grant

Write-Host "Temporary permission registered." -ForegroundColor Green
Write-Host ("Grant ID : {0}" -f $result.grantId)
Write-Host ("Action   : {0}" -f $result.actionKey)
Write-Host ("Decision : {0}" -f ($(if ($result.allowed) { "Allow" } else { "Deny" })))
Write-Host ("User SID : {0}" -f $result.subjectId)
Write-Host ("Expires  : {0} UTC" -f $result.expiresAtUtc)
Write-Host "The Windows agent reloads the effective permission automatically." -ForegroundColor DarkGray
