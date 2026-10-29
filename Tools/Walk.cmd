@echo off
setlocal
rem Walk.cmd - ask the pilot to walk somewhere and watch the log.
rem   Walk.cmd 20                 20 yd straight ahead
rem   Walk.cmd 1977.5,232         world position
rem   Walk.cmd 20 -Seconds 120    watch longer
rem   Walk.cmd 20 -Root "F:\...\net10.0-windows7.0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Walk.ps1" %*
exit /b %errorlevel%
