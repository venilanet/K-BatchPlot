@echo off
setlocal
cd /d "%~dp0"

echo.
echo  ============================================================
echo    MPlot  /  One-click installer
echo  ============================================================
echo.
echo  Please wait. The installer will find your AutoCAD/TArch,
echo  build the plugin locally, and deploy it automatically.
echo  (No Visual Studio, no SDK download, no internet needed.)
echo.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo  [OK] Installer finished. Read the messages above.
) else (
  echo  [X] Installer stopped with code %RC%.
  echo      Details were written to install.log in this folder.
)
echo.
pause
exit /b %RC%
