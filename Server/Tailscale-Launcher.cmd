@echo off
setlocal
title Duluka Account - Tailscale Funnel
cd /d "%~dp0"
echo.
echo  ===========================================
echo      DULUKA ACCOUNT / TAILSCALE
echo  ===========================================
echo.
echo  [1] Check readiness
echo  [2] Test API security
echo  [3] Open public STAGING server (CBT1 closed)
echo  [4] Stop public STAGING Funnel
echo  [5] Open Portal in browser
echo  [6] Full regression: 59 + 135 tests (isolated)
echo.
choice /c 123456 /n /m "Choose 1-6: "
if errorlevel 6 goto regress
if errorlevel 5 goto portal
if errorlevel 4 goto stop
if errorlevel 3 goto publish
if errorlevel 2 goto verify
if errorlevel 1 goto check
:check
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-tailscale-funnel.ps1" -CheckOnly
goto end
:verify
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-tailscale-api.ps1"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-portal-flow.ps1"
goto end
:publish
echo WARNING: Public STAGING allows temporary tester accounts.
echo Never use a real password or personal details.
choice /c YN /n /m "Continue with PUBLIC TEST? (Y/N) "
if errorlevel 2 goto end
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-tailscale-funnel.ps1" -PublicTest
goto end
:stop
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-tailscale-funnel.ps1" -Stop
goto end
:portal
powershell -NoProfile -Command "$j=(& 'C:\Program Files\Tailscale\tailscale.exe' status --json | ConvertFrom-Json); Start-Process ('https://'+$j.Self.DNSName.TrimEnd('.')+'/')"
goto end
:regress
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-full-regression.ps1"
goto end
:end
echo.
pause
