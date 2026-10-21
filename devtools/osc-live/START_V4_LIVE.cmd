@echo off
setlocal
cd /d "%~dp0"
if not exist "v4-live-updater.cjs" (echo V4 Updater not found & exit /b 1)
node --check "v4-live-updater.cjs" || exit /b 1
echo Starting experimental V4 live updater. Native OSC must be running.
node "v4-live-updater.cjs" --live
echo Updater ended. Check preview-status.json for whether OSC was restored.
pause
