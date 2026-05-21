================================================================
 SharbatlyQMS - LAN deployment
================================================================

What's in this folder:
  SharbatlyQMS\                       <- the published web app
  Install-SharbatlyQMSService.ps1    <- run ONCE as Administrator
  Uninstall-SharbatlyQMSService.ps1  <- to remove later
  Republish.ps1                       <- rebuild + re-publish after code changes

First-time setup (one click):
  1. Open the "deploy" folder.
  2. Right-click  Install-SharbatlyQMSService.ps1
  3. Choose      "Run with PowerShell"
     (if PowerShell is blocked, open an elevated PowerShell window
      and run:  Set-ExecutionPolicy -Scope Process Bypass
      then call the script.)
  4. The installer:
       - opens TCP port 5244 in Windows Firewall
       - registers a Windows Service named "SharbatlyQMS"
         that auto-starts on boot
       - starts the service
       - prints the LAN URL to share with users

LAN URL for users:
  http://192.168.3.192:5244
  (anyone on the 192.168.3.x network can open this in any browser)

Local URL (this PC):
  http://localhost:5244

Service controls (run from an elevated PowerShell):
  Get-Service     SharbatlyQMS    # status
  Start-Service   SharbatlyQMS
  Stop-Service    SharbatlyQMS
  Restart-Service SharbatlyQMS

Where logs go:
  Event Viewer -> Windows Logs -> Application
  (filter by Source = "SharbatlyQMS")

After you change code:
  Run  Republish.ps1  in a normal PowerShell (no admin needed).
  It rebuilds the Release output into SharbatlyQMS\ and restarts
  the service so users see your changes within a few seconds.

To remove everything:
  Right-click Uninstall-SharbatlyQMSService.ps1 -> Run as Administrator.
  The published files are NOT deleted; remove the SharbatlyQMS\ folder
  manually if you no longer need them.
