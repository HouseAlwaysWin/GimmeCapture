@echo off
set VERSION=%1
powershell -ExecutionPolicy Bypass -File "%~dp0release.ps1" %VERSION%
