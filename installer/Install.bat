@echo off
rem Starts Install.ps1 without asking about the PowerShell execution policy. No admin rights needed.
if not exist "%~dp0Install.ps1" (
    echo Install.ps1 is missing. Unzip the whole download first, then double-click Install.bat again.
    pause
    exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" %*
echo.
pause
