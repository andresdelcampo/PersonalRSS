@echo off
setlocal
where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo PersonalRSS deployment requires Windows PowerShell 5.1 or newer.
    pause
    exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Restart-PersonalRSS.ps1"
set "deploy_exit=%ERRORLEVEL%"
echo.
if not "%deploy_exit%"=="0" (
    echo PersonalRSS deployment failed. The details are shown above.
) else (
    echo PersonalRSS deployment completed successfully.
)
pause
exit /b %deploy_exit%
