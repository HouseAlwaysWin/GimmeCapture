@echo off
setlocal
set DOTNET_CLI_TELEMETRY_OPTOUT=1
powershell -ExecutionPolicy Bypass -File "%~dp0release.ps1" "%~1"
exit /b %ERRORLEVEL%
