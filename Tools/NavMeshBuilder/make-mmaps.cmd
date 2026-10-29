@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------------------
rem  ADTs -> .navtile -> .mmtile -> <game dir>\mmaps, on Windows, no toolchain.
rem
rem    make-mmaps.cmd <adt-dir> <map-id> <game-dir>
rem
rem  <adt-dir>   folder with the root ADTs exported from CASC, e.g.
rem              C:\export\world\maps\kalimdor   (Kalimdor_32_32.adt, ...)
rem  <map-id>    1 = Kalimdor, 0 = Eastern Kingdoms (NavGridStore.MapId)
rem  <game-dir>  the folder holding CopilotBuddy.exe / wowb.exe; the mmaps go to
rem              <game-dir>\mmaps because the DLL looks for them next to itself
rem
rem  Example:
rem    make-mmaps.cmd C:\export\kalimdor 1 C:\BBuddy
rem ---------------------------------------------------------------------------

set "HERE=%~dp0"
set "NAVBAKE=%HERE%..\NavGrid\bin-x64\navbake.exe"
set "MMAPBUILD=%HERE%bin-x64\mmapbuild.exe"
set "MMAPCHECK=%HERE%bin-x64\mmapcheck.exe"

if "%~3"=="" (
  echo usage: make-mmaps.cmd ^<adt-dir^> ^<map-id^> ^<game-dir^>
  exit /b 2
)
for %%F in ("%NAVBAKE%" "%MMAPBUILD%" "%MMAPCHECK%") do (
  if not exist %%F (
    echo missing %%F
    echo run build-navigation.sh and the NavMeshBuilder Makefile once, or re-extract the tools.
    exit /b 1
  )
)

set "ADTS=%~1"
set "MAP=%~2"
set "GAME=%~3"
set "TILES=%TEMP%\bbuddy-navtiles-%MAP%"

if not exist "%ADTS%" (
  echo no such ADT folder: %ADTS%
  exit /b 1
)

echo [1/4] clearing %TILES%
if exist "%TILES%" rmdir /s /q "%TILES%"
mkdir "%TILES%" 2>nul

echo [2/4] baking ADTs -^> .navtile
"%NAVBAKE%" bakeall --adts "%ADTS%" --map %MAP% --out "%TILES%"
if errorlevel 1 (
  echo bakeall failed - are the ADTs root ADTs ^(MCNK/MCVT^), not _tex0/_obj0 siblings?
  exit /b 1
)

echo [3/4] building mmaps -^> "%GAME%\mmaps"
if not exist "%GAME%\mmaps" mkdir "%GAME%\mmaps"
"%MMAPBUILD%" --tiles "%TILES%" --map %MAP% --out "%GAME%\mmaps"
if errorlevel 1 (
  echo mmapbuild failed
  exit /b 1
)

echo [4/4] verifying the mesh loads in the same Detour the DLL uses
"%MMAPCHECK%" --mmaps "%GAME%\mmaps" --map %MAP%
if errorlevel 1 (
  echo the DLL would reject these mmaps - do not fly yet
  exit /b 1
)
echo.
echo done. mmaps are in "%GAME%\mmaps"
echo path a known coordinate to be sure the geometry lines up:
echo   "%MMAPCHECK%" --mmaps "%GAME%\mmaps" --map %MAP% --from -614,-4254,39 --to -600,-4200,40
endlocal
