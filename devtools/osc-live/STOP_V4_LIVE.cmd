@echo off
setlocal
cd /d "%~dp0"
> STOP_PREVIEW echo Stop updater and restore the original OSC
echo Stop requested; check preview-status.json for productionRestored=true.
pause
