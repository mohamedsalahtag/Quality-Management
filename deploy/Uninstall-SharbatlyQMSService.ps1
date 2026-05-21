# Uninstall-SharbatlyQMSService.ps1
# Removes the SharbatlyQMS Windows Service and its firewall rule.
# Run AS ADMINISTRATOR.

[CmdletBinding()]
param(
    [string]$ServiceName  = 'SharbatlyQMS',
    [string]$FirewallRule = 'SharbatlyQMS HTTP 5244'
)

$ErrorActionPreference = 'Stop'

$current = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($current)).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: Run as Administrator.' -ForegroundColor Red
    exit 1
}

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "Stopping and removing service '$ServiceName'..." -ForegroundColor Cyan
    if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host 'Service removed.' -ForegroundColor Green
} else {
    Write-Host "No service named '$ServiceName' found." -ForegroundColor Gray
}

$rule = Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue
if ($rule) {
    Write-Host "Removing firewall rule '$FirewallRule'..." -ForegroundColor Cyan
    $rule | Remove-NetFirewallRule
    Write-Host 'Firewall rule removed.' -ForegroundColor Green
} else {
    Write-Host 'No matching firewall rule.' -ForegroundColor Gray
}

Write-Host 'Uninstall complete.' -ForegroundColor Green
