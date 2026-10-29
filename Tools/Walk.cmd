@echo off
rem Walk.cmd - ask the pilot to walk somewhere and watch the log.
rem
rem   Walk.cmd 20                      20 yd straight ahead
rem   Walk.cmd 1977.5,232              world position
rem   Walk.cmd -1977.5,-232            negative coordinates work too
rem   Walk.cmd route:-495.3,-4221.1    plan only, print the route, do not walk
rem   Walk.cmd calibrate               measure the click model (do this first)
rem   Walk.cmd 20 -Seconds 120         watch longer
rem   Walk.cmd 1977,232 -Root "F:\World of Warcraft\patch33\BBuddy-clickaim\bin\x64\Release\net10.0-windows7.0"
rem
rem The goal is handed to PowerShell in an ENVIRONMENT VARIABLE, not as an
rem argument. PowerShell splits an unquoted argument on commas, so
rem "route:-495.3,-4221.1" arrived as two arguments and the second one was read as
rem a parameter named "-4221.1"; a leading minus has the same problem. An
rem environment variable is opaque to both cmd and PowerShell's argument parser.
setlocal
if "%~1"=="" goto usage

rem Walk.ps1 has to be found from wherever this .cmd was copied: the exe folder,
rem the Tools folder, or the repository root.
set "PS1="
if exist "%~dp0Walk.ps1" set "PS1=%~dp0Walk.ps1"
if not defined PS1 if exist "%~dp0Tools\Walk.ps1" set "PS1=%~dp0Tools\Walk.ps1"
if not defined PS1 if exist "%~dp0..\Tools\Walk.ps1" set "PS1=%~dp0..\Tools\Walk.ps1"
if not defined PS1 if exist "%~dp0..\..\Tools\Walk.ps1" set "PS1=%~dp0..\..\Tools\Walk.ps1"
if not defined PS1 (
    echo Walk.ps1 was not found next to %~nx0, in Tools\, or one level up.
    echo Copy Walk.ps1 beside Walk.cmd, or run Tools\Walk.cmd from the build.
    exit /b 3
)

set "CLICKAIM_GOAL=%~1"
set "CLICKAIM_SECONDS="
set "CLICKAIM_ROOT="
shift

:parse
if "%~1"=="" goto run
if /i "%~1"=="-Root" goto root
if /i "%~1"=="-Seconds" goto secs
shift
goto parse

:root
set "CLICKAIM_ROOT=%~2"
shift
shift
goto parse

:secs
set "CLICKAIM_SECONDS=%~2"
shift
shift
goto parse

:run
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
exit /b %errorlevel%

:usage
echo usage: Walk.cmd ^<yards^|x,y^|route:x,y^|calibrate^> [-Seconds N] [-Root "exe folder"]
echo    Walk.cmd calibrate
echo    Walk.cmd route:-495.3,-4221.1
echo    Walk.cmd 20
echo    Walk.cmd 1977.5,232 -Seconds 180
exit /b 2
