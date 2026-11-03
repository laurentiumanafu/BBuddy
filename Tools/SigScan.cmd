@echo off
rem SigScan.cmd - resolve function and global addresses in the attached client by
rem byte pattern (Winkr sig_defs.h table). No Ghidra, no dump: this runs in the
rem client and reports, per pattern, exactly-one / ambiguous / no-match-plus-prefix.
rem
rem Usage: put this in the same folder as wowb.exe (or run anywhere and let it
rem find the exe), start the game, then: Tools\SigScan.cmd
setlocal
set HERE=%~dp0
if exist "%HERE%..\wowb.exe" set WOWB=%HERE%..\wowb.exe
if not defined WOWB if exist "%HERE%..\..\wowb.exe" set WOWB=%HERE%..\..\wowb.exe
if not defined WOWB if exist "wowb.exe" set WOWB=wowb.exe
if not defined WOWB (
  echo Could not find wowb.exe. Run this from the bot folder next to the game exe.
  exit /b 1
)
echo Starting %WOWB% with --sigscan
start "" "%WOWB%" --sigscan
endlocal
