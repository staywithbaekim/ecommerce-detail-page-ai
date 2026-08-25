@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%verify_fixture.ps1" -FixtureRoot "%SCRIPT_DIR%." -RequireOutputs
if errorlevel 1 (
  echo.
  echo TV-11 verification failed. Keep the output folder and take a screenshot of this window.
  pause
  exit /b 1
)
echo.
echo TV-11 automated verification passed. Compare all six JPG files with the six reference files.
pause
