@echo off
REM Double-click me. Triggers ONE UAC prompt -- click Yes -- then republishes
REM SharbatlyQMS and restarts the Windows Service.
PowerShell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restart-After-Changes.ps1"
