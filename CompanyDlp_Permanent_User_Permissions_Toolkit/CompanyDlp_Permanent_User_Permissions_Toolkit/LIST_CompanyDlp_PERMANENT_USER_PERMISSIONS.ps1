param(
    [string]$WindowsUser = "",
    [string]$UserSid = "",
    [string]$ActionKey = "",
    [switch]$IncludeRevoked,
    [string]$ProjectRoot = "."
)

Set-StrictMode -Version 3.0
$ErrorActionPreference = "Stop"


function Resolve-CompanyDlpUserSid {
    param(
        [string]$WindowsUser,
        [string]$ExplicitSid
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitSid)) {
        if ($ExplicitSid -notmatch '^S-\d-\d+(-\d+)+$') {
            throw "Invalid Windows SID: $ExplicitSid"
        }

        return $ExplicitSid.Trim()
    }

    if ([string]::IsNullOrWhiteSpace($WindowsUser)) {
        throw "WindowsUser or UserSid is required."
    }

    $candidates = New-Object System.Collections.Generic.List[string]

    if ($WindowsUser.Contains('\')) {
        $candidates.Add($WindowsUser)
    }
    else {
        $candidates.Add("$env:COMPUTERNAME\$WindowsUser")
        $candidates.Add($WindowsUser)
    }

    foreach ($candidate in $candidates) {
        try {
            $account = New-Object System.Security.Principal.NTAccount($candidate)
            return $account.Translate(
                [System.Security.Principal.SecurityIdentifier]
            ).Value
        }
        catch {
            # Try the next candidate.
        }
    }

    throw "Could not resolve Windows user '$WindowsUser' to a SID."
}

function Resolve-CompanyDlpPolicyPath {
    param([string]$ProjectRoot)

    $root = (Resolve-Path -LiteralPath $ProjectRoot).Path
    $path = Join-Path $root "config\policy.development.json"

    if (-not (Test-Path -LiteralPath $path)) {
        throw "Development policy was not found: $path"
    }

    return $path
}

function Write-CompanyDlpPolicyAtomically {
    param(
        [string]$Path,
        [object]$Policy,
        [string]$BackupSuffix
    )

    $directory = Split-Path -Parent $Path
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $backupPath = "$Path.$BackupSuffix-$timestamp.bak"
    $tempPath = Join-Path $directory (
        ".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($Path), [Guid]::NewGuid().ToString("N")
    )

    Copy-Item -LiteralPath $Path -Destination $backupPath -Force

    $json = $Policy | ConvertTo-Json -Depth 100
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($tempPath, $json, $utf8)

    try {
        Move-Item -LiteralPath $tempPath -Destination $Path -Force
    }
    finally {
        Remove-Item -LiteralPath $tempPath -Force -ErrorAction SilentlyContinue
    }

    return $backupPath
}


$policyPath = Resolve-CompanyDlpPolicyPath -ProjectRoot $ProjectRoot
$policy = Get-Content -LiteralPath $policyPath -Raw | ConvertFrom-Json

$resolvedSid = ""
if (-not [string]::IsNullOrWhiteSpace($WindowsUser) -or
    -not [string]::IsNullOrWhiteSpace($UserSid)) {
    $resolvedSid = Resolve-CompanyDlpUserSid `
        -WindowsUser $WindowsUser `
        -ExplicitSid $UserSid
}

$result = @($policy.permissions.grants) |
    Where-Object {
        [string]$_.source -eq "PermanentPolicy"
    } |
    Where-Object {
        $IncludeRevoked -or
        $null -eq $_.revokedAtUtc -or
        [string]::IsNullOrWhiteSpace([string]$_.revokedAtUtc)
    } |
    Where-Object {
        [string]::IsNullOrWhiteSpace($resolvedSid) -or
        [string]$_.subjectId -eq $resolvedSid
    } |
    Where-Object {
        [string]::IsNullOrWhiteSpace($ActionKey) -or
        [string]$_.actionKey -eq $ActionKey
    } |
    Sort-Object createdAtUtc -Descending |
    ForEach-Object {
        $accountName = ""
        try {
            $sid = New-Object System.Security.Principal.SecurityIdentifier(
                [string]$_.subjectId
            )
            $accountName = $sid.Translate(
                [System.Security.Principal.NTAccount]
            ).Value
        }
        catch {
            $accountName = "(unresolved)"
        }

        [PSCustomObject]@{
            GrantId = $_.grantId
            ActionKey = $_.actionKey
            Decision = $(if ($_.allowed) { "Allow" } else { "Deny" })
            WindowsUser = $accountName
            UserSid = $_.subjectId
            Status = $(if (
                $null -eq $_.revokedAtUtc -or
                [string]::IsNullOrWhiteSpace([string]$_.revokedAtUtc)
            ) { "Active" } else { "Revoked" })
            StartsAtUtc = $_.startsAtUtc
            ExpiresAtUtc = $(if ($null -eq $_.expiresAtUtc) { "Never" } else { $_.expiresAtUtc })
            GrantedBy = $_.grantedBy
            RevokedAtUtc = $_.revokedAtUtc
            RevokedBy = $_.revokedBy
            Reason = $_.reason
        }
    }

if ($result.Count -eq 0) {
    Write-Host "No matching permanent permissions were found." -ForegroundColor Yellow
    return
}

$result | Format-Table `
    GrantId,
    ActionKey,
    Decision,
    WindowsUser,
    Status,
    ExpiresAtUtc `
    -AutoSize `
    -Wrap
