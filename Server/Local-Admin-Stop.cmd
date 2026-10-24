@echo off
setlocal
title Duluka Account - Stop Local Admin
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-local-admin.ps1" -Stop
pause
