@echo off
chcp 65001 >nul 2>&1
REM Legacy entry point kept for compatibility.
REM Build + deploy is now handled by tools/install.ps1 (same as the root one-click installer).
REM It deploys to the trusted Autodesk ApplicationPlugins folder and requests UAC when needed.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\tools\install.ps1" %*
echo.
pause
exit /b %ERRORLEVEL%
