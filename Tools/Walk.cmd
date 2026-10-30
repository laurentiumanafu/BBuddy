@echo off
rem Walk.cmd - ask the pilot to walk somewhere and watch the log.
rem
rem   Walk.cmd 20                      20 yd straight ahead
rem   Walk.cmd 1977.5,232              world position
rem   Walk.cmd -1977.5,-232            negative coordinates work too
rem   Walk.cmd route:-495.3,-4221.1    plan only, print the route, do not walk
rem   Walk.cmd 20 -Seconds 120         watch longer
rem   Walk.cmd 1977,232 -Root "F:\...\net10.0-windows7.0"
rem
rem TWO things about cmd's parser are handled here, because both have produced
rem half-written goals in real runs:
rem
rem  1. A COMMA IS AN ARGUMENT SEPARATOR. "Walk.cmd -623.6,-4286.8" arrives as
rem     %1=-623.6 and %2=-4286.8, so reading only %1 wrote "-623.6" into the request
rem     file and the walk went to a point 4286 yards away from any map. The two halves
rem     are rejoined below when the second one is a number.
rem  2. PowerShell splits an unquoted argument on commas as well, and reads a leading
rem     minus as a parameter name, so the goal is handed over in an ENVIRONMENT
rem     VARIABLE - opaque to both parsers.
setlocal
if "%~1"=="" goto usage

set "ARG1=%~1"
set "ARG2=%~2"
set "CLICKAIM_GOAL=%ARG1%"

rem ---- rejoin a comma-split coordinate --------------------------------------
if "%ARG2%"=="" goto goalready
set "C1=%ARG2:~0,1%"
set "C2=%ARG2:~1,1%"
if "%C1%"=="-" goto maybe_number
if "%C1%"=="." goto maybe_number
echo %C1%| findstr /r "^[0-9]$" >nul || goto goalready
:maybe_number
if "%C1%"=="-" echo %C2%| findstr /r "^[0-9.]$" >nul || goto goalready
set "CLICKAIM_GOAL=%ARG1%,%ARG2%"
shift
:goalready

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

echo Goal: %CLICKAIM_GOAL%

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
echo usage: Walk.cmd ^<yards^|x,y^|route:x,y^> [-Seconds N] [-Root "exe folder"]
echo    Walk.cmd hotspots   walk the list the "Add hotspot" button builds
echo    Walk.cmd profile:C:\path\to\profile.xml   walk a profile's hotspots
echo    Walk.cmd route:-495.3,-4221.1
echo    Walk.cmd 20
echo    Walk.cmd 1977.5,232 -Seconds 180
echo Quotes also work and are never needed: Walk.cmd "-623.6,-4286.8"
exit /b 2
