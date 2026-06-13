# Republish-Remote.ps1
# Stops the SharbatlyQMS Windows Service on a remote host, publishes a fresh
# Release build to its deploy folder via the C$ admin share, restarts the
# service, and verifies HTTP responsiveness.
#
# Mirrors the local Republish.ps1 but for a remote host where this machine
# only has SMB (C$ admin share) + RPC (sc.exe \\computer) access -- WinRM is
# typically not enabled.
#
# Usage:
#   .\Republish-Remote.ps1
#   .\Republish-Remote.ps1 -Server 192.168.3.15 -RemoteRoot 'C:\Websites\QualityManagemet'
#
# Prereqs:
#   - You have administrative rights on the target server (admin via your AD
#     account; SMB C$ share + sc.exe \\computer must work).
#   - .NET 9 SDK on this machine (this is where the publish happens).
#   - ASP.NET Core Runtime 9.0 already installed on the target server.
#   - The Windows Service 'SharbatlyQMS' was previously created on the target
#     server pointing at <RemoteRoot>\deploy\SharbatlyQMS\SharbatlyQMS.Web.exe.

[CmdletBinding()]
param(
    [string]$Server          = '192.168.3.15',
    [string]$ServiceName     = 'SharbatlyQMS',
    [string]$SourceProject   = 'C:\QualityManagemet\app\SharbatlyQMS.Web\SharbatlyQMS.Web.csproj',
    [string]$RemoteRoot      = 'C:\Websites\QualityManagemet',
    [int]   $Port            = 5244,
    [int]   $StartTimeoutSec = 30,
    [int]   $HttpTimeoutSec  = 15
)

$ErrorActionPreference = 'Stop'

# Build the UNC publish target from RemoteRoot. Robocopy/dotnet both accept
# `\\host\C$\...` paths transparently.
$drive  = ($RemoteRoot -split ':')[0]
$tail   = ($RemoteRoot -split ':')[1].TrimStart('\')
$uncPub = "\\$Server\$drive`$\$tail\deploy\SharbatlyQMS"

Write-Host ""
Write-Host "Remote republish target" -ForegroundColor Cyan
Write-Host "  Server      : $Server"
Write-Host "  Service     : $ServiceName"
Write-Host "  Source      : $SourceProject"
Write-Host "  Publish dir : $uncPub"
Write-Host ""

# --- 1. Stop the remote service ---------------------------------------------
Write-Host "[1/4] Stopping '$ServiceName' on $Server ..." -ForegroundColor Cyan
$null = & sc.exe \\$Server stop $ServiceName 2>&1
# Poll until STOPPED or timeout. sc.exe stop returns immediately while the
# service is still STOP_PENDING; we wait so the publish doesn't fail with
# "file in use" on SharbatlyQMS.Web.exe.
$deadline = (Get-Date).AddSeconds($StartTimeoutSec)
do {
    Start-Sleep -Seconds 1
    $state = (& sc.exe \\$Server query $ServiceName 2>&1 | Select-String 'STATE').ToString()
} while ($state -notmatch 'STOPPED|1\s+STOPPED' -and (Get-Date) -lt $deadline)
if ($state -match 'STOPPED|1\s+STOPPED') {
    Write-Host "       Service stopped." -ForegroundColor Gray
} else {
    Write-Host "       Service did not reach STOPPED within ${StartTimeoutSec}s -- proceeding anyway." -ForegroundColor Yellow
}

# --- 2. Publish straight to the remote deploy folder ------------------------
Write-Host "[2/4] Publishing Release build into $uncPub ..." -ForegroundColor Cyan
& dotnet publish $SourceProject -c Release -o $uncPub --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "       Publish FAILED (exit $LASTEXITCODE)." -ForegroundColor Red
    Write-Host "       The service is currently STOPPED. Restart it manually with:" -ForegroundColor Yellow
    Write-Host "           sc.exe \\$Server start $ServiceName" -ForegroundColor Yellow
    exit 1
}

# --- 3. Restart the remote service ------------------------------------------
Write-Host "[3/4] Starting '$ServiceName' on $Server ..." -ForegroundColor Cyan
$null = & sc.exe \\$Server start $ServiceName 2>&1
$deadline = (Get-Date).AddSeconds($StartTimeoutSec)
do {
    Start-Sleep -Seconds 1
    $state = (& sc.exe \\$Server query $ServiceName 2>&1 | Select-String 'STATE').ToString()
} while ($state -notmatch '4\s+RUNNING|RUNNING' -and (Get-Date) -lt $deadline)
if ($state -match 'RUNNING') {
    Write-Host "       Service status: Running" -ForegroundColor Green
} else {
    Write-Host "       Service did not reach RUNNING within ${StartTimeoutSec}s." -ForegroundColor Red
    Write-Host "       Check the remote Event Log (Application channel)." -ForegroundColor Yellow
    exit 1
}

# --- 4. Verify HTTP -----------------------------------------------------------
Write-Host "[4/4] Probing http://${Server}:${Port}/Account/Login ..." -ForegroundColor Cyan
# Wait briefly for the app to bind to the port before the first probe.
Start-Sleep -Seconds 3
$probeOk = $false
try {
    # Cross-version-safe HTTP probe: -SkipHttpErrorCheck is PS 7+ only,
    # so we use the raw HttpClient and let any 2xx-5xx count as "the app
    # answered". Only TCP / DNS failures bubble up as a real error.
    Add-Type -AssemblyName System.Net.Http -ErrorAction SilentlyContinue
    $httpc = New-Object System.Net.Http.HttpClient
    $httpc.Timeout = [TimeSpan]::FromSeconds($HttpTimeoutSec)
    $resp = $httpc.GetAsync("http://${Server}:${Port}/Account/Login").GetAwaiter().GetResult()
    $code = [int]$resp.StatusCode
    if ($code -ge 200 -and $code -lt 400) {
        Write-Host "       HTTP $code -- app is serving." -ForegroundColor Green
        $probeOk = $true
    } else {
        Write-Host "       HTTP $code -- service is up but request returned an error code." -ForegroundColor Yellow
    }
    $httpc.Dispose()
} catch {
    Write-Host "       HTTP probe failed: $($_.Exception.Message)" -ForegroundColor Yellow
    Write-Host "       Service reports RUNNING; check firewall TCP $Port and the app's startup logs." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "Done." -ForegroundColor Cyan
