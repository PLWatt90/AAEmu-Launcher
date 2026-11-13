@echo off
rem Serves the patch folder for the launchers; see Start-PatchServer.ps1. Ctrl+C stops it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-PatchServer.ps1" %*
