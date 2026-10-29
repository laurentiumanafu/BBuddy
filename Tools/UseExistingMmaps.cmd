@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  UseExistingMmaps.cmd - point the x64 Navigation.dll at an mmaps folder that
rem  already exists, instead of baking a new one.
rem
rem    Tools\UseExistingMmaps.cmd "B:\World of Warcraft"
rem    Tools\UseExistingMmaps.cmd "<folder with mmaps>" "<bot folder>"
rem    Tools\UseExistingMmaps.cmd "<folder with mmaps>" "<bot folder>" 1
rem
rem  The DLL resolves "mmaps\" next to itself (MoveMap.cpp:getMmapsDirectory), so
rem  this creates a directory junction there. Nothing is copied: a 3.3.5a mmaps set
rem  is ~2 GB and works fine in place.
rem
rem  Both container kinds and both naming schemes are read:
rem    001.mmap   + 0013232.mmtile        MaNGOS / cmangos / AzerothCore
rem    0001.mmap  + 0001_32_32.mmtile     TrinityCore and newer
rem  Do not mix our own 4x4 output with a stock set in one folder: the tile size
rem  in <map>.mmap (133.33 vs 533.33 yd) has to match every tile beside it.
rem
rem  Third argument is the map id to load-check with mmapcheck; by default it is
rem  taken from the first <id>.mmap found.
rem ---------------------------------------------------------------------------

set "SOURCE=%~1"
set "TARGET=%~2"
set "MAPARG=%~3"
if "%TARGET%"=="" set "TARGET=%~dp0.."
set "REPORT=%~dp0usemmaps.txt"
echo UseExistingMmaps > "%REPORT%"
echo date: %DATE% %TIME% >> "%REPORT%"

if "%SOURCE%"=="" (
    echo usage: UseExistingMmaps.cmd ^<wow install or mmaps folder^> [bot folder] [map id]
    echo.
    echo   Tools\UseExistingMmaps.cmd "B:\World of Warcraft"
    exit /b 2
)
if not exist "%SOURCE%\" (
    echo [x] "%SOURCE%" does not exist
    exit /b 1
)

rem ---- find the folder that actually holds <map>.mmap files -------------------
set "MMAPS="
if exist "%SOURCE%\*.mmap" set "MMAPS=%SOURCE%"
if not defined MMAPS if exist "%SOURCE%\mmaps\*.mmap" set "MMAPS=%SOURCE%\mmaps"
if not defined MMAPS if exist "%SOURCE%\data\mmaps\*.mmap" set "MMAPS=%SOURCE%\data\mmaps"
if not defined MMAPS if exist "%SOURCE%\data\data\mmaps\*.mmap" set "MMAPS=%SOURCE%\data\data\mmaps"
if not defined MMAPS (
    for /f "delims=" %%D in ('dir /s /b /ad "%SOURCE%\mmaps" 2^>nul') do (
        if not defined MMAPS if exist "%%D\*.mmap" set "MMAPS=%%D"
    )
)
if not defined MMAPS (
    echo [x] no folder with *.mmap under "%SOURCE%"
    echo     pass the mmaps folder itself, or run the extractor once
    exit /b 1
)
echo [i] mmaps folder: %MMAPS%
echo [i] mmaps folder: %MMAPS%>> "%REPORT%"

rem ---- inventory. One directory read, no per-file processes: the first version of
rem ---- this script ran two findstr per tile file and took minutes on 6000 tiles.
set /a TOTAL=0
for %%F in ("%MMAPS%\*.mmtile") do set /a TOTAL+=1
set /a PARAMFILES=0
set "FIRSTMAP="
for %%F in ("%MMAPS%\*.mmap") do (
    set /a PARAMFILES+=1
    if not defined FIRSTMAP set "FIRSTMAP=%%~nxF"
)
echo [i] %PARAMFILES% params files, %TOTAL% tile files
echo [i] %PARAMFILES% params files, %TOTAL% tile files>> "%REPORT%"
if not defined FIRSTMAP (
    echo [x] no <map>.mmap in that folder - tiles without params are never opened
    exit /b 1
)
echo [i] first params file: %FIRSTMAP%

set "LIST=%TEMP%\bbuddy-mmtiles.txt"
dir /b "%MMAPS%\*.mmtile" > "%LIST%" 2>nul
set /a CLASSIC=0
set /a TRINITY=0
for /f %%C in ('findstr /r /c:"^[0-9][0-9][0-9][0-9][0-9][0-9][0-9]\.mmtile$" "%LIST%" ^| find /c /v ""') do set /a CLASSIC=%%C
for /f %%C in ('findstr /r /c:"^[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]\.mmtile$" "%LIST%" ^| find /c /v ""') do set /a TRINITY=%%C
echo [i] names: %CLASSIC% classic (0013232.mmtile), %TRINITY% TrinityCore (0001_32_32.mmtile)
echo [i] names: %CLASSIC% classic, %TRINITY% TrinityCore>> "%REPORT%"
if %CLASSIC% gtr 0 if %TRINITY% gtr 0 (
    echo [!] both schemes present. If this is one extractor's output for two different
    echo     maps that is fine; if it is two sets for the same map, the folder mixes
    echo     tile sizes and queries will fail. Check with the load check below.
)

rem ---- map id for the load check ---------------------------------------------
rem Kalimdor (map 1) first, because that is the map the bot runs on; otherwise the
rem first params file in the folder.
set "STEM=%FIRSTMAP:.mmap=%"
if "%MAPARG%"=="" (
    if exist "%MMAPS%\001.mmap" set "MAPARG=1"
    if not defined MAPARG if exist "%MMAPS%\0001.mmap" set "MAPARG=1"
    if not defined MAPARG set "MAPARG=%STEM%"
)
set "S=%MAPARG%"
:striplead
if "%S:~0,1%"=="0" (
    set "S=%S:~1%"
    goto :striplead
)
if "%S%"=="" set "S=0"
set "MAPID=%S%"
echo [i] load check will use map %MAPID%

rem ---- junction it next to every Navigation.dll under the target -------------
set FOUND=0
for /f "delims=" %%D in ('dir /s /b "%TARGET%\Navigation.dll" 2^>nul') do (
    call :LinkFolder "%%~dpD"
)
if "%FOUND%"=="0" (
    echo [!] no Navigation.dll under "%TARGET%" - linking "%TARGET%\mmaps" anyway
    echo     (build the bot once; the junction has to sit beside the DLL)
    call :LinkFolder "%TARGET%\"
)

rem ---- load every tile of that map with the same Detour build -----------------
echo.
if not exist "%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" (
    echo [i] Tools\NavMeshBuilder\bin-x64\mmapcheck.exe not found, skipping the load check
    goto :Done
)
echo [i] loading every tile of map %MAPID% with the same Detour build:
"%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" --mmaps "%MMAPS%" --map %MAPID% > "%TEMP%\bbuddy-mmapcheck.txt" 2>&1
set "RC=%ERRORLEVEL%"
type "%TEMP%\bbuddy-mmapcheck.txt"
type "%TEMP%\bbuddy-mmapcheck.txt" >> "%REPORT%"
if not "%RC%"=="0" (
    echo.
    echo [!] exit code %RC% - some tiles were refused. The counts above say how many;
    echo     a set that is entirely refused usually means the tiles and the params
    echo     beside them come from different builds ^(or 4x4 vs stock sizes^).
) else (
    echo.
    echo [i] every tile of map %MAPID% was accepted by dtNavMesh.
)

:Done
echo.
echo done. The DLL now reads %MMAPS%
echo report: %REPORT%
endlocal
exit /b 0

:LinkFolder
set "DESTDIR=%~1"
set "LINK=%DESTDIR%mmaps"
if /i "%LINK%\."=="%MMAPS%\." goto :LinkDone
if exist "%LINK%\" (
    dir /al "%DESTDIR%" 2>nul | findstr /i "mmaps" >nul
    if errorlevel 1 (
        echo [!] %LINK% already exists and is a real folder - left alone
        goto :LinkDone
    )
    rmdir "%LINK%" 2>nul
)
mklink /J "%LINK%" "%MMAPS%" >nul 2>&1
if errorlevel 1 (
    echo [x] mklink failed for %LINK%
) else (
    echo [i] %LINK% -^> %MMAPS%
    echo [i] linked %LINK%>> "%REPORT%"
    set FOUND=1
)
:LinkDone
exit /b 0
