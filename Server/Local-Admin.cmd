@echo off
setlocal
title Duluka Account - Local Admin
cd /d "%~dp0"
echo Starting protected Local Admin on 127.0.0.1:5117 ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-local-admin.ps1"
if errorlevel 1 (
 echo.
 echo ERROR: Local Admin could not start.
 pause
)
