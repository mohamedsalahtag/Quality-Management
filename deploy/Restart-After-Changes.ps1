# Restart-After-Changes.ps1
# Right-click -> "Run with PowerShell" (or right-click -> Run as administrator).
# Stops the SharbatlyQMS service, republishes the Release build into
# C:\QualityManagemet\deploy\SharbatlyQMS, then starts the service again.

$ErrorActionPreference = 'Stop'

# Self-elevate if not already running as admin -- so a single double-click
# triggers ONE UAC prompt and then the whole job runs in the elevated shell.
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Re-launching elevated...' -ForegroundColor Yellow
    Start-Process powershell -Verb RunAs -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath
    )
    exit
}

Write-Host '[1/3] Stopping SharbatlyQMS service...' -ForegroundColor Cyan
Stop-Service SharbatlyQMS -Force
Start-Sleep -Seconds 2

Write-Host '[2/3] Publishing Release build...' -ForegroundColor Cyan
& dotnet publish 'C:\QualityManagemet\app\SharbatlyQMS.Web' -c Release -o 'C:\QualityManagemet\deploy\SharbatlyQMS' --nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host 'dotnet publish FAILED -- starting service back up with the previous build.' -ForegroundColor Red
    Start-Service SharbatlyQMS
    Read-Host 'Press Enter to close'
    exit 1
}

Write-Host '[3/3] Starting service...' -ForegroundColor Cyan
Start-Service SharbatlyQMS
Start-Sleep -Seconds 2
$status = (Get-Service SharbatlyQMS).Status
Write-Host "Service status: $status" -ForegroundColor Green
Write-Host 'LAN URL: http://192.168.3.192:5244' -ForegroundColor Green
Read-Host 'Press Enter to close'
