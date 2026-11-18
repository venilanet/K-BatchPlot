@echo off
setlocal
cd /d "%~dp0"

echo.
echo  ============================================================
echo    MPlot  /  Disable (uninstall)
echo  ============================================================
echo.
echo  This does NOT delete anything. It renames the bundle folder
echo  so AutoCAD will stop loading it. You can restore it anytime.
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\uninstall.ps1"
echo.
pause
