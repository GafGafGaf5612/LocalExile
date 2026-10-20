@echo off
rem Starts the servers and the client through scripts\dev\live.ps1 (the only safe way to start the client, see
rem Agents\GettingStarted.md). Without arguments it captures walk maps (-Walkmaps); any arguments go to live.ps1 as they
rem are, e.g. "run.bat -NoClient" or "run.bat -Only Gateway". Logs: logs\
cd /d "%~dp0"
if "%~1"=="" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev\live.ps1" -Walkmaps
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\dev\live.ps1" %*
)
if errorlevel 1 echo live.ps1 failed: see the message above and logs\
pause
