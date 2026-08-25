@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0verify_fixture.ps1" -Prepare
if errorlevel 1 (
  echo.
  echo Preparation failed. Do not edit the baseline folder.
  pause
  exit /b 1
)
start "" "%~dp0work"
echo.
echo Working copy prepared. Follow README.md in this folder.
pause
