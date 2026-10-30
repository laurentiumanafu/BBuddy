@echo off
rem Build.cmd - build the x64 bot and show only errors, warnings and the verdict.
rem
rem Usage: Tools\Build.cmd
rem The full log is kept next to this script as build-last.log.
setlocal
set HERE=%~dp0
set CSPROJ=%HERE%..\CopilotBuddy.csproj
set LOG=%HERE%build-last.log

if not exist "%CSPROJ%" (
  echo Cannot find %CSPROJ%
  exit /b 1
)

dotnet build "%CSPROJ%" -c Release -p:Platform=x64 -v:minimal -nologo >"%LOG%" 2>&1
set RC=%ERRORLEVEL%

findstr /i /c:"error CS" /c:"error NETSDK" /c:"error MSB" /c:"error MSBUILD" /c:"warning CS" /c:"Build succeeded" /c:"Build FAILED" "%LOG%"

if %RC% NEQ 0 (
  echo.
  echo Build failed. Full log: %LOG%
)
exit /b %RC%
