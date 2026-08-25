@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify_fixture.ps1" -RequireEdited
if errorlevel 1 (
  echo.
  echo Verification failed. Read the error above and keep all files for review.
  pause
  exit /b 1
)
start "" "%~dp0work"
echo.
echo Automated checks passed. Return the PSD, evidence JSON, and reopening screenshot.
pause
