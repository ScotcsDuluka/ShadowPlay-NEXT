@echo off
rem run-server.cmd — double-click launcher for Duluka.Server (works from any CWD).
rem "public" is blocked because account credentials require HTTPS; see README.md
if /i "%~1"=="public" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server.ps1" -Public %2 %3 %4
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-server.ps1" %*
)
pause
