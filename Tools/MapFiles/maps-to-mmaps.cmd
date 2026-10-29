@echo off
setlocal enabledelayedexpansion
rem ---------------------------------------------------------------------------
rem  server .map files -> .navtile -> .mmtile -> <game dir>\mmaps
rem
rem    maps-to-mmaps.cmd <repack data dir> <map-id> <game dir> [x,y,z]
rem
rem  <repack data dir>  the folder that holds "maps", "vmaps" and "mmaps", e.g.
rem                     B:\World of Warcraft\ForeverRepack\data
rem  <map-id>           1 = Kalimdor, 0 = Eastern Kingdoms
rem  <game dir>         folder holding CopilotBuddy.exe; mmaps go to <game dir>\mmaps
rem
rem  This route needs no CASC extractor: the repack already carries the terrain
rem  in the server's .map format. What it gives you is terrain - hills, cliffs,
rem  holes, shorelines. Buildings and bridges live in vmaps and are not included.
rem
rem  The optional x,y,z is a spot you know the height of - your logged position.
rem  The tool prints the height it baked there; if that disagrees with the Z the
rem  client shows, the map files belong to a different version of the world and the
rem  CASC route is the accurate one for this client.
rem
rem  Example:
rem    maps-to-mmaps.cmd "B:\World of Warcraft\ForeverRepack\data" 1 C:\BBuddy -614.8,-4254.2,39
rem ---------------------------------------------------------------------------

set "HERE=%~dp0"
set "MAPDUMP=%HERE%bin-x64\mapdump.exe"
set "MMAPBUILD=%HERE%..\NavMeshBuilder\bin-x64\mmapbuild.exe"
set "MMAPCHECK=%HERE%..\NavMeshBuilder\bin-x64\mmapcheck.exe"
set "NAVBAKE=%HERE%..\NavGrid\bin-x64\navbake.exe"

if "%~3"=="" (
  echo usage: maps-to-mmaps.cmd ^<repack data dir^> ^<map-id^> ^<game dir^>
  exit /b 2
)
for %%F in ("%MAPDUMP%" "%MMAPBUILD%") do (
  if not exist %%F (
    echo missing %%F
    exit /b 1
  )
)

set "DATA=%~1"
set "MAP=%~2"
set "GAME=%~3"
set "MAPS=%DATA%\maps"
set "TILES=%TEMP%\bbuddy-maptiles-%MAP%"

if not exist "%MAPS%" (
  echo no maps folder: %MAPS%
  echo pass the data folder that contains maps\, vmaps\ and mmaps\.
  exit /b 1
)

echo [1/3] converting %MAPS% -^> %TILES%
if exist "%TILES%" rmdir /s /q "%TILES%"
mkdir "%TILES%" 2>nul
"%MAPDUMP%" convert --maps "%MAPS%" --out "%TILES%" --map %MAP%
if errorlevel 1 (
  echo.
  echo conversion failed - here is what the files actually contain:
  "%MAPDUMP%" info --maps "%MAPS%"
  exit /b 1
)

echo [1b/3] checking that the tiles line up with each other
"%MAPDUMP%" check --tiles "%TILES%" --map %MAP%
if errorlevel 1 (
  echo.
  echo The tiles are contiguous in the source and not in the converted set. That is a
  echo naming or axis problem, not a terrain problem - do not build mmaps from this.
  exit /b 1
)

echo [2/3] building mmaps -^> "%GAME%\mmaps"
if not exist "%GAME%\mmaps" mkdir "%GAME%\mmaps"
"%MMAPBUILD%" --tiles "%TILES%" --map %MAP% --out "%GAME%\mmaps"
if errorlevel 1 (
  echo mmapbuild failed
  exit /b 1
)

echo [3/3] checking the mesh loads
if exist "%MMAPCHECK%" (
  "%MMAPCHECK%" --mmaps "%GAME%\mmaps" --map %MAP%
) else (
  echo mmapcheck.exe not found, skipping the load check
)

echo.
echo done. tiles for map %MAP% are in "%GAME%\mmaps"
if not "%~4"=="" (
  echo.
  echo height check at %~4 - compare with the Z the bot logs:
  "%NAVBAKE%" probe --tiles "%TILES%" --map %MAP% %~4
)
endlocal
