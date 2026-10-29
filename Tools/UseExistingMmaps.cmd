@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  UseExistingMmaps.cmd - point the x64 Navigation.dll at an mmaps folder that
rem  already exists, instead of baking a new one.
rem
rem    Tools\UseExistingMmaps.cmd "B:\World of Warcraft"
rem    Tools\UseExistingMmaps.cmd "<mmaps folder>" "<bot folder>" [map id]
rem
rem  Quote paths that contain spaces. If the second argument is missing or does not
rem  exist (an unquoted path is split at its first space), the folder that contains
rem  the mmaps folder is used instead - in a normal install that is the exe folder,
rem  which is where the DLL looks anyway.
rem
rem  The DLL resolves "mmaps\" next to itself (MoveMap.cpp:getMmapsDirectory), so
rem  this creates a directory junction there. Nothing is copied: a 3.3.5a mmaps set
rem  is ~2 GB and works fine in place.
rem
rem  Read: <map>.mmap + <map><x><y>.mmtile   (MaNGOS, cmangos, AzerothCore)
rem        <map>.mmap + <map>_<x>_<y>.mmtile (TrinityCore and newer)
rem  Do not mix our own 4x4 output with a stock set in one folder: the tile size in
rem  <map>.mmap (133.33 vs 533.33 yd) has to match every tile beside it.
rem ---------------------------------------------------------------------------

set "SOURCE=%~1"
set "TARGET=%~2"
set "MAPARG=%~3"
set "REPORT=%~dp0usemmaps.txt"
echo UseExistingMmaps > "%REPORT%"
echo date: %DATE% %TIME% >> "%REPORT%"

if "%SOURCE%"=="" (
    echo usage: UseExistingMmaps.cmd ^<wow install or mmaps folder^> ["bot folder"] [map id]
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

rem The folder that holds the mmaps folder is the one the DLL sits in.
for %%P in ("%MMAPS%\..") do set "PARENT=%%~fP"

rem ---- where to put the junction ---------------------------------------------
if not defined TARGET set "TARGET=%PARENT%"
if not exist "%TARGET%\" (
    echo [!] "%TARGET%" is not a folder - an unquoted path with spaces is split here.
    echo     using "%PARENT%" instead
    set "TARGET=%PARENT%"
)
echo [i] bot folder: %TARGET%
echo [i] bot folder: %TARGET%>> "%REPORT%"

rem ---- inventory. One directory read plus two findstr passes: the first version
rem ---- ran two findstr per tile file and took minutes on 6000 tiles.
set /a TOTAL=0
for %%F in ("%MMAPS%\*.mmtile") do set /a TOTAL+=1
set /a PARAMS=0
set "FIRSTMAP="
for %%F in ("%MMAPS%\*.mmap") do (
    set /a PARAMS+=1
    if not defined FIRSTMAP set "FIRSTMAP=%%~nxF"
)
echo [i] %PARAMS% params files, %TOTAL% tile files
echo [i] %PARAMS% params files, %TOTAL% tile files>> "%REPORT%"
if not defined FIRSTMAP (
    echo [x] no ^<map^>.mmap in that folder - tiles without params are never opened
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

rem ---- map id for the load check ---------------------------------------------
rem Kalimdor (map 1) first, because that is the map the bot runs on; otherwise the
rem first params file in the folder. Leading zeros are stripped with set /a on a
rem "1"-prefixed copy: set /a alone would read 0010 as octal 8, and a for/goto loop
rem here is what produced "map of" the first time this was written.
if not defined MAPARG (
    if exist "%MMAPS%\001.mmap" set "MAPARG=1"
    if not defined MAPARG if exist "%MMAPS%\0001.mmap" set "MAPARG=1"
    if not defined MAPARG set "MAPARG=%FIRSTMAP:.mmap=%"
)
set "DIGITS=%MAPARG%"
rem set /a on a non-numeric string is an error and leaves MAPID undefined, which is
rem how "map of" reached mmapcheck. Validate, then default to Kalimdor.
echo %DIGITS%| findstr /r /c:"^[0-9][0-9]*$" >nul || set "DIGITS=1"
set /a MAPID=1%DIGITS%-1000
if %MAPID% gtr 999 set /a MAPID=1%DIGITS%-10000
if %MAPID% lss 0 set /a MAPID=1%DIGITS%-100
if %MAPID% lss 0 set "MAPID=1"
echo [i] load check will use map %MAPID%

rem ---- junction it next to every Navigation.dll under the target -------------
set FOUND=0
for /f "delims=" %%D in ('dir /s /b "%TARGET%\Navigation.dll" 2^>nul') do (
    call :LinkFolder "%%~dpD"
)
if "%FOUND%"=="0" (
    if /i "%TARGET%\"=="%PARENT%\" (
        echo [i] %TARGET%\mmaps is already where the DLL looks - nothing to link
    ) else (
        echo [!] no Navigation.dll under "%TARGET%" - linking "%TARGET%\mmaps" anyway
        echo     (build the bot once so the junction sits beside the DLL)
        call :LinkFolder "%TARGET%\"
    )
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
echo done. The DLL reads: %MMAPS%
echo report: %REPORT%
endlocal
exit /b 0

:LinkFolder
set "DESTDIR=%~1"
set "LINK=%DESTDIR%mmaps"
if /i "%LINK%"=="%MMAPS%" goto :LinkDone
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
    echo [x] mklink failed for "%LINK%"
) else (
    echo [i] "%LINK%" -^> "%MMAPS%"
    echo [i] linked %LINK%>> "%REPORT%"
    set FOUND=1
)
:LinkDone
exit /b 0
