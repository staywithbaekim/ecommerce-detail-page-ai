@echo off
setlocal
set "SCRIPT_DIR=%~dp0"
C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%verify_fixture.ps1" -FixtureRoot "%SCRIPT_DIR%."
if errorlevel 1 (
  echo.
  echo TV-06 input check failed. Do not run Photoshop. Take a screenshot of this window.
  pause
  exit /b 1
)
echo.
echo TV-06 inputs passed. Continue with README-TV06.md.
pause
