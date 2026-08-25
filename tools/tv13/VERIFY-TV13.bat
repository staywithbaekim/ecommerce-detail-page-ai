@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%verify_tv13.ps1" -FixtureRoot "%SCRIPT_DIR%." -RequireRecovery
if errorlevel 1 (
  echo.
  echo TV-13 verification failed. Keep both TV-13 output folders and take a screenshot.
  pause
  exit /b 1
)
echo.
echo TV-13 failure cleanup and continued-run verification passed.
pause
