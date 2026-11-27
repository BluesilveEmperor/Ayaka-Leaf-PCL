@echo off
rem Ayaka Collab Lite launcher - starts the PowerShell GUI with execution policy bypass
setlocal
set "SCRIPT=%~dp0CollabLite.ps1"
if not exist "%SCRIPT%" (
    echo [Error] CollabLite.ps1 not found next to this launcher.
    pause
    exit /b 1
)
start "" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%SCRIPT%"
endlocal
