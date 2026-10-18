@echo off
setlocal

rem Historical diagnostic wrapper only; this is not the NVIDIA OSC host build.
set "SCRIPT_DIR=%~dp0"
for %%I in ("%SCRIPT_DIR%..\..") do set "OSC_ROOT=%%~fI"
set "TEST_OUT=%OSC_ROOT%\build_test\NVIDIA OSC_Test"
set "TEST_OBJ=%OSC_ROOT%\build_test\obj"
if not exist "%TEST_OUT%" mkdir "%TEST_OUT%"
if not exist "%TEST_OBJ%" mkdir "%TEST_OBJ%"

set "VCVARS=%ProgramFiles%\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" set "VCVARS=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
if not exist "%VCVARS%" (
  echo ERROR: Visual Studio vcvars64.bat not found.
  exit /b 2
)
call "%VCVARS%" >nul 2>&1
if errorlevel 1 exit /b %errorlevel%

cl /nologo /O2 /W3 /DUNICODE "%SCRIPT_DIR%osc_child_process_diagnostic.cpp" /Fe:"%TEST_OUT%\OSC Child Process Diagnostic.exe" /Fo:"%TEST_OBJ%\osc_child_process_diagnostic.obj" /link /SUBSYSTEM:WINDOWS shell32.lib user32.lib
if errorlevel 1 exit /b %errorlevel%
echo Built diagnostic wrapper only. It is not deployed to the live runtime.
