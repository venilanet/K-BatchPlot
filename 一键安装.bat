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

rem L9: call the 64-bit powershell explicitly. From a 64-bit cmd,
rem %SystemRoot%\System32 is already 64-bit; from a 32-bit process
rem System32 redirects to SysWOW64, so use the Sysnative alias
rem to reach the real 64-bit powershell.
set "PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo  [OK] Installer finished. Read the messages above.
) else if "%RC%"=="2" (
  echo  [i] Switched to the admin window, check the result there.
) else (
  echo  [X] Installer stopped with code %RC%.
  echo      Details were written to install.log in this folder.
)
echo.
pause
exit /b %RC%
