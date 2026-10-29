@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  SelfCheck.cmd - one pass over everything that can be checked without the game.
rem
rem    Tools\SelfCheck.cmd                 (uses .\mmaps or .\Lib\x64\mmaps)
rem    Tools\SelfCheck.cmd "B:\...\mmaps"
rem
rem  Writes the whole report to selfcheck.txt next to this script and prints it.
rem  Every line says what was run, so nothing here can be mistaken for a result
rem  that was not actually produced.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0.."
set "REPORT=%~dp0selfcheck.txt"
set "MMAPS=%~1"
if "%MMAPS%"=="" if exist "%ROOT%\mmaps\*.mmap" set "MMAPS=%ROOT%\mmaps"
if "%MMAPS%"=="" if exist "%ROOT%\Lib\x64\mmaps\*.mmap" set "MMAPS=%ROOT%\Lib\x64\mmaps"

echo Navigation self-check > "%REPORT%"
echo date: %DATE% %TIME% >> "%REPORT%"
echo repo: %ROOT% >> "%REPORT%"
echo. >> "%REPORT%"

call :Say "1. the DLL the bot will load"
if exist "%ROOT%\Lib\x64\Navigation.dll" (
    call :ReportSize "%ROOT%\Lib\x64\Navigation.dll"
    for /f "skip=1 tokens=*" %%H in ('certutil -hashfile "%ROOT%\Lib\x64\Navigation.dll" MD5 2^>nul') do (
        if not defined DLLHASH call :Report "  md5: %%H" & set "DLLHASH=1"
    )
    rem 3c87b8b7ade732612a4af120208471c5 is the build that reads stock 3.3.5a
    rem mmtiles; anything older will reject them, so the hash printed above is the
    rem first thing to compare.
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
set /a CLASSIC=0
set /a TRINITY=0
for %%F in ("%MMAPS%\*.mmtile") do (
    set "NAME=%%~nxF"
    echo !NAME! | findstr /r "^[0-9][0-9][0-9][0-9][0-9][0-9][0-9]\.mmtile$" >nul && set /a CLASSIC+=1
    echo !NAME! | findstr /r "^[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]\.mmtile$" >nul && set /a TRINITY+=1
)
call :Report "  tile files: !CLASSIC! classic, !TRINITY! TrinityCore"
for %%F in ("%MMAPS%\*.mmap") do call :Report "  params: %%~nxF"
call :Report ""

:NavMeshBuilder
call :Say "3. loading every tile of map 1 with mmapcheck"
if not exist "%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" (
    call :Report "  [x] Tools\NavMeshBuilder\bin-x64\mmapcheck.exe is missing"
    goto :Logs
)
if "%MMAPS%"=="" (
    call :Report "  skipped: no mmaps folder"
    goto :Logs
)
"%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" --mmaps "%MMAPS%" --map 1 >> "%REPORT%" 2>&1
call :Report "  mmapcheck exit code: !ERRORLEVEL!  (0 = every tile accepted)"
call :Report ""

:Logs
call :Say "4. what the bot logged about the navmesh"
set "LASTLOG="
for /f "delims=" %%L in ('dir /b /o-d "%ROOT%\Logs\*.log" 2^>nul') do (
    if not defined LASTLOG set "LASTLOG=%ROOT%\Logs\%%L"
)
if defined LASTLOG (
    call :Report "  log: %LASTLOG%"
    findstr /i "Navigation.dll NavMesh navmesh mmaps" "%LASTLOG%" >> "%REPORT%" 2>&1
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

:Say
call :Report ""
call :Report "== %~1"
exit /b 0

:Report
echo %~1>> "%REPORT%"
exit /b 0

:ReportSize
for %%S in ("%~1") do call :Report "  file: %%~nxS, %%~zS bytes"
exit /b 0
