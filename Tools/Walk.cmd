@echo off
rem Walk.cmd - ask the pilot to walk somewhere and watch the log.
rem
rem   Walk.cmd 20                      20 yd straight ahead
rem   Walk.cmd 1977.5,232              world position
rem   Walk.cmd -1977.5,-232            negative coordinates work too
rem   Walk.cmd 20 -Seconds 120         watch longer
rem   Walk.cmd 1977,232 -Root "F:\World of Warcraft\patch33\BBuddy-clickaim\bin\x64\Release\net10.0-windows7.0"
rem
rem The goal is passed as -Goal on purpose: PowerShell would read a leading
rem "-1977.5,-232" as a parameter name and refuse to run, which is exactly what
rem happened the first time this was tried.
setlocal
if "%~1"=="" goto usage
set "GOAL=%~1"
shift
set "ARGS="

:parse
if "%~1"=="" goto run
if /i "%~1"=="-Root" goto root
if /i "%~1"=="-Seconds" goto secs
set "ARGS=%ARGS% %~1"
shift
goto parse

:root
set "ARGS=%ARGS% -Root "%~2""
shift
shift
goto parse

:secs
set "ARGS=%ARGS% -Seconds %~2"
shift
shift
goto parse

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Walk.ps1" -Goal "%GOAL%" %ARGS%
exit /b %errorlevel%

:usage
echo usage: Walk.cmd ^<yards^|x,y^> [-Seconds N] [-Root "folder with CopilotBuddy.exe"]
echo    Walk.cmd 20
echo    Walk.cmd 1977.5,232
echo    Walk.cmd -1977.5,-232 -Seconds 120
exit /b 2
