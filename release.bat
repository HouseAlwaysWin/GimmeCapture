@echo off
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set VERSION=%1
powershell -ExecutionPolicy Bypass -File "%~dp0release.ps1" %VERSION%
