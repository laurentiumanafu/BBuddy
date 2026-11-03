@echo off
rem Build.cmd - build the x64 bot, printing ERRORS ONLY.
rem
rem   Tools\Build.cmd                 build, show only errors + the verdict
rem   Tools\Build.cmd -w              also print warnings
rem   Tools\Build.cmd -full           print the whole build log
rem
rem Warnings are suppressed by the compiler itself (-clp:ErrorsOnly), not filtered
rem out of the output afterwards, so nothing is hidden: they are still counted and
rem the full log is written to Tools\build-last.log either way.
setlocal enabledelayedexpansion
set HERE=%~dp0
set CSPROJ=%HERE%..\CopilotBuddy.csproj
set LOG=%HERE%build-last.log
set SHOWWARN=0
set FULL=0

:args
if "%~1"=="" goto build
if /i "%~1"=="-w" set SHOWWARN=1
if /i "%~1"=="--warnings" set SHOWWARN=1
if /i "%~1"=="-full" set FULL=1
if /i "%~1"=="--full" set FULL=1
shift
goto args

:build
if not exist "%CSPROJ%" (
  echo Cannot find %CSPROJ%
  exit /b 1
)

dotnet build "%CSPROJ%" -c Release -p:Platform=x64 -nologo -v:minimal -clp:ErrorsOnly >"%LOG%" 2>&1
set RC=%ERRORLEVEL%

if "%FULL%"=="1" (
  type "%LOG%"
  exit /b %RC%
)

findstr /i /c:"error CS" /c:"error NETSDK" /c:"error MSB" /c:"error MSBUILD" "%LOG%"
if "%SHOWWARN%"=="1" findstr /i /c:"warning CS" "%LOG%"

set WARNS=0
for /f %%W in ('findstr /i /c:"warning CS" "%LOG%" ^| find /c /v ""') do set WARNS=%%W

if %RC% EQU 0 (
  echo Build succeeded. %WARNS% warnings suppressed - run with -w to see them, or -full for the log.
) else (
  echo.
  echo Build FAILED. Full log: %LOG%
)
exit /b %RC%
