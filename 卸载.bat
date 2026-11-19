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

rem L9: call the 64-bit powershell explicitly (same reason as in the install bat).
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\uninstall.ps1"
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo  [OK] Disable finished.
) else (
  echo  [X] Disable stopped with code %RC%.
)
echo.
pause
exit /b %RC%
