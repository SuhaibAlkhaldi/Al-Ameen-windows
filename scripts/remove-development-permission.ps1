param(
    [Parameter(Mandatory = $true)]
    [Guid]$GrantId
)

$ErrorActionPreference = "Stop"
Invoke-RestMethod `
    -Method Delete `
    -Uri ("http://127.0.0.1:5055/api/v1/development/temporary-permissions/{0}" -f $GrantId) | Out-Null
Write-Host "Temporary permission revoked: $GrantId" -ForegroundColor Green
