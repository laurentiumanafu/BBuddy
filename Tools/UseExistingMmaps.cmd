@echo off
setlocal EnableExtensions EnableDelayedExpansion
rem ---------------------------------------------------------------------------
rem  UseExistingMmaps.cmd - point the x64 Navigation.dll at an mmaps folder that
rem  already exists, instead of baking a new one.
rem
rem  The DLL resolves "mmaps\" next to itself (MoveMap.cpp:getMmapsDirectory), so
rem  this creates a directory junction there. Nothing is copied: a 3.3.5a mmaps set
rem  is ~2 GB and works fine in place.
rem
rem    Tools\UseExistingMmaps.cmd "B:\World of Warcraft"
rem    Tools\UseExistingMmaps.cmd "B:\World of Warcraft\ForeverRepack\data\mmaps" C:\BBuddy
rem
rem  Both container kinds and both naming schemes are read:
rem    001.mmap   + 0013232.mmtile        MaNGOS / cmangos / AzerothCore
rem    0001.mmap  + 0001_32_32.mmtile     TrinityCore and newer
rem  Do not mix our own 4x4 output with a stock set in one folder: the tile size
rem  in <map>.mmap (133.33 vs 533.33 yd) has to match every tile beside it.
rem ---------------------------------------------------------------------------

set "SOURCE=%~1"
set "TARGET=%~2"
if "%TARGET%"=="" set "TARGET=%~dp0.."

if "%SOURCE%"=="" (
    echo usage: UseExistingMmaps.cmd ^<wow install or mmaps folder^> [bot folder]
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
    for /d /r "%SOURCE%" %%D in (mmaps) do (
        if not defined MMAPS if exist "%%D\*.mmap" set "MMAPS=%%D"
    )
)
if not defined MMAPS (
    echo [x] no folder with *.mmap under "%SOURCE%"
    echo     pass the mmaps folder itself, or run the extractor once
    exit /b 1
)
echo [i] mmaps folder: %MMAPS%

for %%F in ("%MMAPS%\*.mmap") do set "FIRSTMAP=%%~nxF"
echo [i] first params file: %FIRSTMAP%

rem ---- report what is there, so the scheme is obvious before launching -------
set COUNT_CLASSIC=0
set COUNT_TRINITY=0
for %%F in ("%MMAPS%\*.mmtile") do (
    set "NAME=%%~nxF"
    echo !NAME! | findstr /r "^[0-9][0-9][0-9][0-9][0-9][0-9][0-9]\.mmtile$" >nul && set /a COUNT_CLASSIC+=1
    echo !NAME! | findstr /r "^[0-9][0-9][0-9][0-9]_[0-9][0-9]_[0-9][0-9]\.mmtile$" >nul && set /a COUNT_TRINITY+=1
)
echo [i] tile files: %COUNT_CLASSIC% classic (0013232.mmtile), %COUNT_TRINITY% TrinityCore (0001_32_32.mmtile)
if %COUNT_CLASSIC%==0 if %COUNT_TRINITY%==0 (
    echo [x] no tile files matched either naming scheme
    exit /b 1
)

rem ---- junction it next to every Navigation.dll under the target -------------
set FOUND=0
for /r "%TARGET%" %%D in (Navigation.dll) do (
    call :LinkFolder "%%~dpD"
)
if "%FOUND%"=="0" (
    echo [!] no Navigation.dll under "%TARGET%" - linking "%TARGET%\mmaps" anyway
    call :LinkFolder "%TARGET%\"
)

rem ---- validate with mmapcheck if it is there --------------------------------
if exist "%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" (
    echo.
    echo [i] loading every tile of the first map with the same Detour build:
    "%~dp0NavMeshBuilder\bin-x64\mmapcheck.exe" --mmaps "%MMAPS%" --map 1
    if errorlevel 1 (
        echo [!] some tiles were rejected - see the counts above
    )
) else (
    echo [i] mmapcheck.exe not found, skipping the load check
)

echo.
echo done. The DLL now reads %MMAPS%
endlocal
exit /b 0

:LinkFolder
set "DESTDIR=%~1"
set "LINK=%DESTDIR%mmaps"
if /i "%LINK%"=="%MMAPS%" exit /b 0
if exist "%LINK%\" (
    dir /al "%DESTDIR%" 2>nul | findstr /i "mmaps" >nul
    if errorlevel 1 (
        echo [!] %LINK% already exists and is a real folder - left alone
        exit /b 0
    )
    echo [i] replacing existing junction %LINK%
    rmdir "%LINK%" 2>nul
)
mklink /J "%LINK%" "%MMAPS%" >nul 2>&1
if errorlevel 1 (
    echo [x] mklink failed for %LINK% - run this from an elevated prompt
) else (
    echo [i] %LINK% -^> %MMAPS%
    set FOUND=1
)
exit /b 0
