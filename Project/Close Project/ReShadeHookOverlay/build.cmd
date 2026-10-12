@echo off
setlocal
call "C:\Visual Studio\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b %ERRORLEVEL%

set "MSBUILD=C:\Visual Studio\MSBuild\Current\Bin\MSBuild.exe"
"%MSBUILD%" "%~dp0ReShadeHookOverlay.vcxproj" /m /t:Build /p:Configuration=Release /p:Platform=x64 /v:minimal
if errorlevel 1 exit /b %ERRORLEVEL%

"%MSBUILD%" "%~dp0ReShadeHookOverlay.vcxproj" /m /t:Build /p:Configuration=Release /p:Platform=Win32 /v:minimal
if errorlevel 1 exit /b %ERRORLEVEL%

echo.
echo Build complete:
echo   %~dp0build\x64\Release\NVIDIA Game Filter Hook.addon
echo   %~dp0build\Win32\Release\NVIDIA Game Filter Hook.addon
exit /b 0
