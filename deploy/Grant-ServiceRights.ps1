# Grant-ServiceRights.ps1
#
# RUN ONCE WITH ADMIN. Grants Interactive Users (i.e. whoever is currently
# logged into this PC) the right to STOP / START the SharbatlyQMS Windows
# service without needing UAC elevation every time.
#
# After this script succeeds, Republish.ps1 can stop/start the service
# silently. No more UAC popups during deploys.
#
# Technical note: edits the service security descriptor via `sc.exe sdset`.
# The DACL is persisted in the registry under
#   HKLM\SYSTEM\CurrentControlSet\Services\SharbatlyQMS\Security
# and survives reboots and service restarts. It is reset only if the
# service is uninstalled (Uninstall-SharbatlyQMSService.ps1 + reinstall).

[CmdletBinding()]
param(
    [string]$ServiceName = 'SharbatlyQMS'
)

$ErrorActionPreference = 'Stop'

# ---- Self-elevate if needed (single UAC prompt) ----
$me = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $me.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Requesting admin elevation (one-time, for SDDL edit)...' -ForegroundColor Cyan
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath
    )
    exit $LASTEXITCODE
}

Write-Host "Granting Interactive Users start/stop rights on service '$ServiceName'..." -ForegroundColor Cyan

# Read the current security descriptor (SDDL string).
$current = ((sc.exe sdshow $ServiceName) -join '').Trim()
if ([string]::IsNullOrWhiteSpace($current)) {
    Write-Host "ERROR: Could not read SDDL for service '$ServiceName'. Is the service installed?" -ForegroundColor Red
    exit 1
}
Write-Host "Before: $current"

# Quick fingerprint check: does the IU ACE already have RP (start) AND WP (stop)?
if ($current -match '\(A;;[A-Z]*RP[A-Z]*WP[A-Z]*;;;IU\)' -or
    $current -match '\(A;;[A-Z]*WP[A-Z]*RP[A-Z]*;;;IU\)') {
    Write-Host "OK: Interactive Users already have Start/Stop rights. Nothing to do." -ForegroundColor Green
    exit 0
}

# Service-control ACE flags reminder:
#   CC = SERVICE_QUERY_CONFIG     DC = SERVICE_CHANGE_CONFIG
#   LC = SERVICE_QUERY_STATUS     SW = SERVICE_ENUMERATE_DEPENDENTS
#   RP = SERVICE_START            WP = SERVICE_STOP
#   DT = SERVICE_PAUSE_CONTINUE   LO = SERVICE_INTERROGATE
#   CR = SERVICE_USER_DEFINED_CONTROL
#   RC = READ_CONTROL
# Default IU ACE on Windows is `(A;;CCLCSWLOCRRC;;;IU)` -- we just need to
# add RP + WP. The new IU ACE keeps every existing right and adds those two.
$newIuAce = '(A;;CCLCSWRPWPLOCRRC;;;IU)'

if ($current -match '\(A;;[A-Z]*;;;IU\)') {
    # Replace existing IU ACE in-place.
    $new = $current -replace '\(A;;[A-Z]*;;;IU\)', $newIuAce
} elseif ($current -match '^(D:[^S]*)(S:.*)$') {
    # No IU ACE yet -- inject one just before the SACL section.
    $new = $matches[1] + $newIuAce + $matches[2]
} else {
    # Pathological: no SACL at all -- append.
    $new = $current + $newIuAce
}

Write-Host "After : $new"

# Apply the new DACL.
$cmdOutput = sc.exe sdset $ServiceName $new 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAILED: sc.exe sdset exited with code $LASTEXITCODE" -ForegroundColor Red
    Write-Host $cmdOutput
    exit 1
}

Write-Host ''
Write-Host 'DONE. Future Republish.ps1 runs will not need UAC prompts.' -ForegroundColor Green

# When auto-launched via Start-Process -Verb RunAs, the elevated window
# closes immediately on exit -- give the user a brief pause to read the
# result before it vanishes.
if ($Host.Name -eq 'ConsoleHost' -and -not $env:CI) {
    Start-Sleep -Seconds 2
}
