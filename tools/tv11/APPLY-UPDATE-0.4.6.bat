@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0apply_update_0_4_6.ps1"
if errorlevel 1 (
  echo.
  echo Update failed. No Photoshop input files were modified. Take a screenshot of this window.
) else (
  echo.
  echo Update completed successfully.
)
pause
