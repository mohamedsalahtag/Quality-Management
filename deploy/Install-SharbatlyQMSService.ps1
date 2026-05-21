# Install-SharbatlyQMSService.ps1
# One-shot installer: firewall rule + Windows Service for SharbatlyQMS.
# Run this PowerShell file AS ADMINISTRATOR (right-click -> Run with PowerShell
# after opening an elevated PowerShell, or right-click -> Run as administrator).

[CmdletBinding()]
param(
    [string]$ServiceName  = 'SharbatlyQMS',
    [string]$DisplayName  = 'Sharbatly QMS',
    [string]$Description  = 'Sharbatly Quality Management System web app.',
    [int]   $Port         = 5244,
    [string]$BinaryPath   = 'C:\QualityManagemet\deploy\SharbatlyQMS\SharbatlyQMS.Web.exe',
    [string]$FirewallRule = 'SharbatlyQMS HTTP 5244'
)

$ErrorActionPreference = 'Stop'

# --- Admin check ------------------------------------------------------------
$current = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($current)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'ERROR: This script must be run as Administrator.' -ForegroundColor Red
    Write-Host 'Right-click the file and choose "Run as administrator", or run from an elevated PowerShell.' -ForegroundColor Yellow
    exit 1
}

# --- Verify the published binary exists -------------------------------------
if (-not (Test-Path $BinaryPath)) {
    Write-Host "ERROR: Cannot find $BinaryPath" -ForegroundColor Red
    Write-Host 'Run `dotnet publish -c Release -o C:\QualityManagemet\deploy\SharbatlyQMS` first.' -ForegroundColor Yellow
    exit 1
}

# --- Firewall rule ----------------------------------------------------------
Write-Host "[1/4] Configuring firewall rule '$FirewallRule' on TCP $Port..." -ForegroundColor Cyan
$existingRule = Get-NetFirewallRule -DisplayName $FirewallRule -ErrorAction SilentlyContinue
if ($existingRule) {
    Write-Host '       Rule already exists; refreshing.' -ForegroundColor Gray
    $existingRule | Remove-NetFirewallRule
}
New-NetFirewallRule `
    -DisplayName $FirewallRule `
    -Description 'Allow LAN access to the SharbatlyQMS web app.' `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort $Port `
    -Profile Domain,Private | Out-Null
Write-Host '       Firewall rule installed.' -ForegroundColor Green

# --- Stop + remove any prior service ----------------------------------------
Write-Host "[2/4] Removing any prior '$ServiceName' service..." -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Write-Host '       Stopping existing service...' -ForegroundColor Gray
        Stop-Service -Name $ServiceName -Force
        # Give Windows a moment to release the port + exe lock.
        $deadline = (Get-Date).AddSeconds(15)
        while ((Get-Service -Name $ServiceName).Status -ne 'Stopped' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
        }
    }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
    Write-Host '       Prior service removed.' -ForegroundColor Green
} else {
    Write-Host '       No prior service.' -ForegroundColor Gray
}

# --- Create the service -----------------------------------------------------
Write-Host "[3/4] Creating service '$ServiceName'..." -ForegroundColor Cyan
New-Service `
    -Name           $ServiceName `
    -BinaryPathName "`"$BinaryPath`"" `
    -DisplayName    $DisplayName `
    -Description    $Description `
    -StartupType    Automatic | Out-Null
# Auto-restart on failure (1st + 2nd failure after 5s, reset count every 60s)
sc.exe failure $ServiceName reset= 60 actions= restart/5000/restart/5000/restart/10000 | Out-Null
Write-Host '       Service created (Automatic startup, auto-restart on failure).' -ForegroundColor Green

# --- Start --------------------------------------------------------------------
Write-Host "[4/4] Starting service..." -ForegroundColor Cyan
Start-Service -Name $ServiceName
Start-Sleep -Seconds 3
$status = (Get-Service -Name $ServiceName).Status
if ($status -eq 'Running') {
    Write-Host "       Service is RUNNING." -ForegroundColor Green
} else {
    Write-Host "       Service status: $status (expected Running)." -ForegroundColor Yellow
    Write-Host '       Check Event Viewer -> Windows Logs -> Application for startup errors.' -ForegroundColor Yellow
}

# --- Done --------------------------------------------------------------------
$lanIp = (Get-NetIPAddress -AddressFamily IPv4 |
          Where-Object { $_.PrefixOrigin -eq 'Dhcp' -and $_.IPAddress -notlike '169.*' } |
          Select-Object -First 1).IPAddress
Write-Host ''
Write-Host '================================================================' -ForegroundColor Green
Write-Host '  SharbatlyQMS service installed.' -ForegroundColor Green
Write-Host '================================================================' -ForegroundColor Green
Write-Host "  LAN URL:   http://${lanIp}:$Port"
Write-Host "  Local URL: http://localhost:$Port"
Write-Host ''
Write-Host '  Service controls (any elevated PowerShell):'
Write-Host "    Start-Service $ServiceName"
Write-Host "    Stop-Service  $ServiceName"
Write-Host "    Restart-Service $ServiceName"
Write-Host "    Get-Service   $ServiceName"
Write-Host ''
Write-Host '  Logs:  Event Viewer -> Windows Logs -> Application (Source: SharbatlyQMS)' -ForegroundColor Gray
