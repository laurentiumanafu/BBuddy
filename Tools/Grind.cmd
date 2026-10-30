@echo off
rem Grind.cmd - circle a grind profile's hotspots and fight what it meets.
rem
rem   Tools\Grind.cmd "F:\Profiles\Horde12-60_cloth.xml"
rem   Tools\Grind.cmd "F:\Profiles\Horde12-60_cloth.xml" -Seconds 300
rem   Tools\Walk.cmd stop            stop grinding
rem
rem The profile's <GrindArea> hotspots are the ring; the area nearest the player is
rem the one used. Hostility is the CLIENT's judgement (TAB only selects enemies),
rem because a faction id cannot be read on this build; the profile's TargetMinLevel/
rem TargetMaxLevel and <Factions> are logged but not enforced.
rem
rem Attacking is action-bar keys 1-5, T, F, and a key is believed only when the
rem selected target's health actually drops. If none of them removes health the log
rem says so - combat then needs the routine, which does not run in WowB mode.
setlocal
if "%~1"=="" goto usage
set "GOAL=grind:%~1"
shift
call "%~dp0Walk.cmd" "%GOAL%" %*
exit /b %errorlevel%
:usage
echo usage: Grind.cmd ^<profile.xml^> [-Seconds N]
echo    Grind.cmd "F:\Profiles\Horde12-60_cloth.xml"
echo    Walk.cmd stop        stop grinding
exit /b 2
