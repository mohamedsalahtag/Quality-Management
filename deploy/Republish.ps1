# Republish.ps1
# Rebuild SharbatlyQMS.Web in Release mode and refresh the deploy folder.
# Stops the Windows Service while files are being replaced, then restarts.
#
# UAC: once Grant-ServiceRights.ps1 has been run (one-time), the current
# user has direct stop/start rights and this script needs NO elevation.
# If rights are missing on first run, we self-launch the grant script
# (one final UAC prompt ever) and retry.

[CmdletBinding()]
param(
    [string]$Project    = 'C:\QualityManagemet\app\SharbatlyQMS.Web',
    [string]$Output     = 'C:\QualityManagemet\deploy\SharbatlyQMS',
    [string]$ServiceName = 'SharbatlyQMS'
)

$ErrorActionPreference = 'Stop'
$grantScript = Join-Path $PSScriptRoot 'Grant-ServiceRights.ps1'

function Stop-ServiceSilently {
    param([string]$Name)
    try {
        Stop-Service -Name $Name -Force -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

function Start-ServiceSilently {
    param([string]$Name)
    try {
        Start-Service -Name $Name -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

Write-Host "[1/3] Stopping service '$ServiceName' (if running)..." -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$wasRunning = $false
if ($svc -and $svc.Status -eq 'Running') {
    $wasRunning = $true
    if (-not (Stop-ServiceSilently -Name $ServiceName)) {
        # Likely no direct rights yet. Run the one-time grant script (one
        # UAC prompt), then retry. After this, future runs are silent.
        if (Test-Path $grantScript) {
            Write-Host '       Access denied. Running one-time service-rights grant...' -ForegroundColor Yellow
            & $grantScript -ServiceName $ServiceName
            if (-not (Stop-ServiceSilently -Name $ServiceName)) {
                Write-Host 'ERROR: still cannot stop the service. Aborting.' -ForegroundColor Red
                exit 1
            }
        } else {
            Write-Host "ERROR: Stop-Service denied and $grantScript not found." -ForegroundColor Red
            exit 1
        }
    }
    # Wait for the port + DLLs to be released.
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    Write-Host '       Service stopped.' -ForegroundColor Green
} else {
    Write-Host '       Service not running; nothing to stop.' -ForegroundColor Gray
}

Write-Host "[2/3] Publishing Release build into $Output ..." -ForegroundColor Cyan
& dotnet publish "$Project" -c Release -o "$Output" --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: dotnet publish failed.' -ForegroundColor Red
    if ($wasRunning) {
        Start-ServiceSilently -Name $ServiceName | Out-Null
    }
    exit 1
}

Write-Host "[3/3] Restarting service..." -ForegroundColor Cyan
if ($wasRunning -or (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    if (-not (Start-ServiceSilently -Name $ServiceName)) {
        Write-Host 'ERROR: Start-Service failed. Run deploy\Grant-ServiceRights.ps1 once with admin, then retry.' -ForegroundColor Red
        exit 1
    }
    Start-Sleep -Seconds 2
    $status = (Get-Service -Name $ServiceName).Status
    Write-Host "       Service status: $status" -ForegroundColor Green
} else {
    Write-Host "       Service '$ServiceName' is not installed yet. Run Install-SharbatlyQMSService.ps1 once." -ForegroundColor Yellow
}

Write-Host 'Done.' -ForegroundColor Green
