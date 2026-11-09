@echo off
setlocal
rem Build without modifying the last released Koi.exe in the repository root.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
set "BUILD_RESULT=%ERRORLEVEL%"
if /i "%~1"=="--no-pause" goto done
if defined CI goto done
pause
:done
exit /b %BUILD_RESULT%
