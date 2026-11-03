@echo off
rem StopTest.cmd - can a key press cancel a click-walk on this build?
rem
rem The 12:57 run clicked once and the character walked 120.4 yd with three stop-key
rem taps having no effect. The cause was lParam 0x00010001 - repeat 1, SCAN CODE 0 -
rem which a client may ignore; ClickAimInput now sends the real scan code. This tests
rem the result: centre click, wait for the walk, tap one key, measure what still happens.
rem
rem Usage: Tools\StopTest.cmd
setlocal
set HERE=%~dp0
if not defined WOWB if exist "%HERE%..\wowb.exe" set WOWB=%HERE%..\wowb.exe
if not defined WOWB if exist "%HERE%..\..\wowb.exe" set WOWB=%HERE%..\..\wowb.exe
if not defined WOWB if exist "wowb.exe" set WOWB=wowb.exe
if not defined WOWB (
  echo Could not find wowb.exe. Run this from the bot folder next to the game exe.
  exit /b 1
)
echo Starting %WOWB% with --clickstop
start "" "%WOWB%" --clickstop
endlocal
