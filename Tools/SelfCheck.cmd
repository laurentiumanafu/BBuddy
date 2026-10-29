@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  SelfCheck.cmd - one pass over everything that can be checked without the game.
rem
rem    Tools\SelfCheck.cmd                (finds mmaps beside the exe)
rem    Tools\SelfCheck.cmd "B:\...\mmaps"
rem    Tools\SelfCheck.cmd "B:\...\mmaps" 1        (map id for the load check)
rem
rem  Writes the whole report to Tools\selfcheck.txt and prints it. Every line says
rem  what produced it, so nothing here can be mistaken for a result that was not
rem  actually produced.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0.."
set "REPORT=%~dp0selfcheck.txt"
set "MMAPS=%~1"
set "MAPID=%~2"
if "%MMAPS%"=="" if exist "%ROOT%\mmaps\*.mmap" set "MMAPS=%ROOT%\mmaps"
if "%MMAPS%"=="" if exist "%ROOT%\Lib\x64\mmaps\*.mmap" set "MMAPS=%ROOT%\Lib\x64\mmaps"

echo Navigation self-check > "%REPORT%"
echo date: %DATE% %TIME% >> "%REPORT%"
echo repo: %ROOT% >> "%REPORT%"
echo. >> "%REPORT%"

call :Say "1. the DLL the bot will load"
if exist "%ROOT%\Lib\x64\Navigation.dll" (
    for %%S in ("%ROOT%\Lib\x64\Navigation.dll") do call :Report "  file: %%~nxS, %%~zS bytes"
    rem 3c87b8b7ade732612a4af120208471c5 is the build that reads stock 3.3.5a mmtiles.
    rem Anything older refuses them, so this hash is the first thing to compare.
    call :Hash "%ROOT%\Lib\x64\Navigation.dll"
) else (
    call :Report "  [x] Lib\x64\Navigation.dll is missing"
)
call :Report ""

call :Say "2. the mmaps folder"
if "%MMAPS%"=="" (
    call :Report "  [x] no mmaps folder found - run Tools\UseExistingMmaps.cmd first"
    goto :NavMeshBuilder
)
call :Report "  folder: %MMAPS%"

rem One directory read and two findstr passes. Spawning findstr per file turns a
rem 6000-tile folder into twelve thousand processes, which looks like a hang.
set /a TOTAL=0
for %%F in ("%MMAPS%\*.mmtile") do set /a TOTAL+=1
set /a PARAMS=0
set "FIRSTMAP="
for %%F in ("%MMAPS%\*.mmap") do (
    set /a PARAMS+=1
    if not defined FIRSTMAP set "FIRSTMAP=%%~nxF"
)
set "LIST=%TEMP%\bbuddy-selfcheck-mmtiles.txt"
dir /b "%MMAPS%\*.mmtile" > "%LIST%" 2>nul
set /a CLASSIC=0
set /a TRINITY=0
for /f %%C in ('findstr /r /c:"^[0-9][0-9][0-9][0-9][0-9][0-9][0-9]\.mmtile$" "%LIST%" ^| find /c /v ""') do set /a CLASSIC=%%C
for /f %%C in ('findstr /r /c:"^[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]\.mmtile$" "%LIST%" ^| find /c /v ""') do set /a TRINITY=%%C
call :Report "  tile files: %TOTAL% (%CLASSIC% classic, %TRINITY% TrinityCore)"
call :Report "  params files: %PARAMS%, first is %FIRSTMAP%"
call :Report ""

:NavMeshBuilder
call :Say "3. loading every tile of one map with mmapcheck"
if not exist "%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" (
    call :Report "  [x] Tools\NavMeshBuilder\bin-x64\mmapcheck.exe is missing"
    goto :Logs
)
if "%MMAPS%"=="" (
    call :Report "  skipped: no mmaps folder"
    goto :Logs
)
if not defined MAPID call :MapIdFromParams "%FIRSTMAP%"
echo %MAPID%| findstr /r /c:"^[0-9][0-9]*$" >nul || set "MAPID=1"
call :Report "  map %MAPID%"
"%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" --mmaps "%MMAPS%" --map %MAPID% >> "%REPORT%" 2>&1
call :Report "  mmapcheck exit code: %ERRORLEVEL%  (0 = every tile accepted)"
call :Report ""

:Logs
call :Say "4. what the bot logged about the navmesh"
set "LASTLOG="
for /f "delims=" %%L in ('dir /b /o-d "%ROOT%\Logs\*.log" 2^>nul') do (
    if not defined LASTLOG set "LASTLOG=%ROOT%\Logs\%%L"
)
if defined LASTLOG (
    call :Report "  log: %LASTLOG%"
    findstr /i "Navigation.dll NavMesh MeshProbe mmaps" "%LASTLOG%" >> "%REPORT%" 2>&1
    if errorlevel 1 call :Report "  (no navmesh lines in that log)"
) else (
    call :Report "  no %ROOT%\Logs\*.log - run the bot once"
)
call :Report ""

echo.
echo report written to %REPORT%
type "%REPORT%"
endlocal
exit /b 0

:MapIdFromParams
rem Map id from a params file name ("001.mmap" or "0001.mmap").
rem
rem Not a strip-leading-zeros loop: `if ... set ... & goto` runs the goto whatever
rem the condition said, so "001" rewrote to "1", jumped anyway, and spun forever.
rem Leading zeros are also why set /a is not used directly - it reads 0010 as octal
rem 8. Prefixing with 1 and subtracting the width sidesteps both.
set "MAPID=1"
set "DIGITS=%~1"
if "%DIGITS%"=="" goto :eof
for /f "delims=." %%A in ("%DIGITS%") do set "DIGITS=%%A"
echo %DIGITS%| findstr /r /c:"^[0-9][0-9]*$" >nul || goto :eof
set /a MAPID=1%DIGITS%-1000
if %MAPID% gtr 999 set /a MAPID=1%DIGITS%-10000
if %MAPID% lss 0 set "MAPID=1"
goto :eof

:Hash
set "HASHED="
for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%~1" MD5 2^>nul') do (
    if not defined HASHED call :Report "  md5: %%H" & set "HASHED=1"
)
exit /b 0

:Say
call :Report ""
call :Report "== %~1"
exit /b 0

:Report
echo %~1>> "%REPORT%"
exit /b 0
