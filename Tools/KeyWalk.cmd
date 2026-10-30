@echo off
rem KeyWalk.cmd - BloogBot-style movement: write the heading, hold the forward key.
rem No clicks, no camera, no pixels. Calibrates the client angle convention first,
rem then walks to x,y in 12 yd steps, re-steering every 250 ms.
rem
rem Usage: Tools\KeyWalk.cmd -623.6,-4286.8
rem        Tools\KeyWalk.cmd            (walks 20 yd on the current facing)
setlocal
set HERE=%~dp0
if not defined WOWB if exist "%HERE%..\wowb.exe" set WOWB=%HERE%..\wowb.exe
if not defined WOWB if exist "%HERE%..\..\wowb.exe" set WOWB=%HERE%..\..\wowb.exe
if not defined WOWB if exist "wowb.exe" set WOWB=wowb.exe
if not defined WOWB (
  echo Could not find wowb.exe. Run this from the bot folder next to the game exe.
  exit /b 1
)
if "%~1"=="" (
  echo Starting %WOWB% with --keywalk
  start "" "%WOWB%" --keywalk
) else (
  echo Starting %WOWB% with --keywalk %1
  start "" "%WOWB%" --keywalk %1
)
endlocal
