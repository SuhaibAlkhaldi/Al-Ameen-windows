param([int]$Take = 50)
$ErrorActionPreference = "Stop"

$uri = "http://127.0.0.1:5055/api/v1/development/events?take={0}" -f ([Math]::Min(1000, [Math]::Max(1, $Take)))
$events = @(
    Invoke-RestMethod -Uri $uri
) | Where-Object { $null -ne $_ }

if ($events.Count -eq 0) {
    Write-Host "No synchronized security events were found in the Development Mock Server." -ForegroundColor Yellow
    Write-Host "Events may still exist in the encrypted local outbox." -ForegroundColor DarkGray
    exit 0
}

Write-Host ("Synchronized events found: {0}" -f $events.Count) -ForegroundColor Green
$events |
    Select-Object occurredAtUtc, username, machineName, actionKey, eventType, decision, reasonCode,
        @{Name="Method";Expression={$_.details.method}},
        @{Name="Resource";Expression={$_.resource.name}},
        @{Name="Destination";Expression={$_.destination.value}} |
    Format-Table -AutoSize -Wrap
