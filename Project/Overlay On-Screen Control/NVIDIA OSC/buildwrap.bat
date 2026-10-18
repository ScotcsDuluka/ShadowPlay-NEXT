@echo off
call "C:\Visual Studio\VC\Auxiliary\Build\vcvars64.bat" >nul 2>&1
cl /O2 /W3 /DUNICODE childwrap.cpp /Fe:childwrap.exe /link /SUBSYSTEM:WINDOWS shell32.lib user32.lib
