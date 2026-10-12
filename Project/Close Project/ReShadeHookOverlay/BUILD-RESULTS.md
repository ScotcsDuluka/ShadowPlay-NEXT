# Hook Overlay build evidence

Date: 2026-10-12
Module: Project/NvOverlay/ReShadeHookOverlay
Build script: build.cmd
Configuration: Release
Build includes explicit callback/overlay teardown and first-present/first-panel ReShade log messages.

## Results

| Target | Result | PE machine | Size (bytes) | SHA-256 |
|---|---|---:|---:|---|
| x64 | Build passed | 0x8664 (AMD64) | 14848 | 04A94DE662666630EC5B75750DE1C791D1B57FB4A6AFB95CDFAB3F05C3C375E5 |
| Win32 | Build passed | 0x014C (i386) | 12800 | F4F39088A21CB0EE84EAFE417C1E2CDDF47724DF4338B61FD82A9290495A9FC2 |

The x64 binary exports NAME and DESCRIPTION, as expected by ReShade's add-on loader.

Build log: logs/build-2026-10-12.log

## Warning

MSBuild emitted warning MSB8029 about the intermediate/output directory and a temporary directory. Both builds exited successfully and the PE headers and output files were verified. The warning is recorded rather than suppressed; its cause has not been resolved.

## Runtime status

Build verification is complete. Loading the add-on into an actual game with ReShade and confirming the panel appears and responds to input has not yet been tested. Do not mark in-game integration as PASS until that test is observed.
