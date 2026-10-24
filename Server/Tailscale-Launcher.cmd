@echo off
setlocal
title Duluka Account - Tailscale
cd /d "%~dp0"
echo.
echo ==============================================
echo    SHADOWPLAY NEXT / DULUKA ACCOUNT
echo ==============================================
echo.
echo [1] Check shared Account Portal readiness
echo [2] Run security + account integration tests
echo [3] START public Account Portal (MAIN database)
echo [4] STOP public Tailscale Funnel
echo [5] Open Portal in browser
echo [6] Full server regression (isolated)
echo [7] Temporary STAGING only (separate database)
echo.
choice /c 1234567 /n /m "Choose 1-7: "
if errorlevel 7 goto staging
if errorlevel 6 goto regression
if errorlevel 5 goto portal
if errorlevel 4 goto stop
if errorlevel 3 goto main
if errorlevel 2 goto tests
if errorlevel 1 goto check
:check
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-shared-account-funnel.ps1" -CheckOnly
goto end
:tests
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-tailscale-api.ps1" -BinaryPath "%~dp0Duluka.Server\bin\TailscaleShared\Duluka.Server.exe"
if errorlevel 1 goto end
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-portal-flow.ps1" -BinaryPath "%~dp0Duluka.Server\bin\TailscaleShared\Duluka.Server.exe"
goto end
:main
echo PUBLIC Account Portal points at the MAIN database.
echo CBT1 and new public account registration are disabled.
choice /c YN /n /m "Start shared Account Portal? (Y/N) "
if errorlevel 2 goto end
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-shared-account-funnel.ps1" -Start
goto end
:stop
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-shared-account-funnel.ps1" -Stop
goto end
:portal
powershell -NoProfile -Command "$j=(& 'C:\Program Files\Tailscale\tailscale.exe' status --json | ConvertFrom-Json); Start-Process ('https://'+$j.Self.DNSName.TrimEnd('.')+'/')"
goto end
:regression
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify-full-regression.ps1"
goto end
:staging
echo WARNING: STAGING is a separate disposable database.
echo Stop the MAIN Funnel before starting STAGING.
choice /c YN /n /m "Start temporary STAGING Funnel? (Y/N) "
if errorlevel 2 goto end
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-tailscale-funnel.ps1" -PublicTest
goto end
:end
echo.
pause
