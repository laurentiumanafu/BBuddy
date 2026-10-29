@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------------------
rem  One command from the game's data folder to a working navmesh.
rem
rem    build-navmesh.cmd "<wow dir>" <map-id> <ref x,y,z> [<game dir>]
rem
rem  <wow dir>   e.g. "C:\Games\WoW"  (the folder that contains Data\data)
rem  <map-id>    1 = Kalimdor, 0 = Eastern Kingdoms
rem  <ref>       a coordinate you know in game, e.g. -614.8,-4254.2,39
rem  <game dir>  where CopilotBuddy.exe lives; defaults to this checkout's bin
rem
rem  Steps, each of which stops the script on failure:
rem    1. cascdump   CASC store -> ADT files
rem    2. navbake    ADT files  -> .navtile
rem    3. mmapbuild  .navtile   -> .mmtile + <map>.mmap
rem    4. mmapcheck  load them with the same Detour the DLL uses
rem ---------------------------------------------------------------------------

set "HERE=%~dp0"
set "CASC=%HERE%CascAdt\bin-x64\cascdump.exe"
set "NAVBAKE=%HERE%NavGrid\bin-x64\navbake.exe"
set "MMAPBUILD=%HERE%NavMeshBuilder\bin-x64\mmapbuild.exe"
set "MMAPCHECK=%HERE%NavMeshBuilder\bin-x64\mmapcheck.exe"

if "%~3"=="" (
  echo usage: build-navmesh.cmd "^<wow dir^>" ^<map-id^> ^<ref x,y,z^> [^<game dir^>]
  echo   example: build-navmesh.cmd "C:\Games\WoW" 1 -614.8,-4254.2,39 "C:\BBuddy"
  exit /b 2
)

set "WOW=%~1"
set "MAP=%~2"
set "REF=%~3"
if "%~4"=="" (set "GAME=%HERE%..\bin\Debug\net10.0-windows") else (set "GAME=%~4")

for %%F in ("%CASC%" "%NAVBAKE%" "%MMAPBUILD%" "%MMAPCHECK%") do (
  if not exist %%F (
    echo missing %%F
    echo run "make win" in Tools\CascAdt and Tools\NavMeshBuilder, or re-extract these tools.
    exit /b 1
  )
)

set "DATA=%WOW%\Data\data"
if not exist "%DATA%" set "DATA=%WOW%\Data"
if not exist "%DATA%" (
  echo cannot find Data\data under "%WOW%"
  exit /b 1
)

set "WORK=%TEMP%\bbuddy-nav-%MAP%"
set "ADT=%WORK%\adt"
set "TILES=%WORK%\navtile"
if exist "%WORK%" rmdir /s /q "%WORK%"
mkdir "%ADT%" 2>nul
mkdir "%TILES%" 2>nul

echo [1/4] reading %DATA%
"%CASC%" list --data "%DATA%" --verbose
if errorlevel 1 (
  echo.
  echo no index entries could be read. Here is the header report, keep it:
  "%CASC%" inspect --data "%DATA%"
  exit /b 1
)
echo.
echo       extracting ADTs, this reads the whole store once
"%CASC%" dump --data "%DATA%" --out "%ADT%" --map %MAP% --ref %REF%
if errorlevel 1 (
  echo nothing was extracted - see the note at the end of the output above
  exit /b 1
)

echo.
echo [2/4] baking .navtile
"%NAVBAKE%" bakeall --adts "%ADT%" --map %MAP% --out "%TILES%"
if errorlevel 1 exit /b 1

echo.
echo [3/4] building mmaps into "%GAME%\mmaps"
if not exist "%GAME%\mmaps" mkdir "%GAME%\mmaps" 2>nul
"%MMAPBUILD%" --tiles "%TILES%" --map %MAP% --out "%GAME%\mmaps"
if errorlevel 1 exit /b 1

echo.
echo [4/4] verifying with the same Detour the DLL uses
"%MMAPCHECK%" --mmaps "%GAME%\mmaps" --map %MAP%
if errorlevel 1 (
  echo the DLL would reject these mmaps
  exit /b 1
)

echo.
echo done.
echo   ADTs     %ADT%
echo   navtiles %TILES%
echo   mmaps    %GAME%\mmaps
echo.
echo check one tile against the game before flying:
echo   "%MMAPCHECK%" --mmaps "%GAME%\mmaps" --map %MAP% --from %REF% --to %REF%
endlocal
