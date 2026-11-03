@echo off
rem DestHunt.cmd - find the field the client walks toward.
rem Clicks once, snapshots the player record while the character walks, and reports
rem every float triple that holds where it stopped but not where it was: the
rem click-to-move destination. Run it on open ground, character standing still.
setlocal
set HERE=%~dp0
if not defined WOWB if exist "%HERE%..\wowb.exe" set WOWB=%HERE%..\wowb.exe
if not defined WOWB if exist "%HERE%..\..\wowb.exe" set WOWB=%HERE%..\..\wowb.exe
if not defined WOWB if exist "wowb.exe" set WOWB=wowb.exe
if not defined WOWB (
  echo Could not find wowb.exe. Run this from the bot folder next to the game exe.
  exit /b 1
)
echo Starting %WOWB% with --clickdest
start "" "%WOWB%" --clickdest
endlocal
