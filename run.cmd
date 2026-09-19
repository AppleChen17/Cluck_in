@echo off
rem Double-clickable wrapper: bypasses the PowerShell execution policy for this one run.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
