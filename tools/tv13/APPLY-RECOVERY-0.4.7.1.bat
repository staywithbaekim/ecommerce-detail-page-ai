@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0apply_update_0_4_7_1.ps1"
if errorlevel 1 (
  echo.
  echo Update did not complete. No Photoshop input files were deleted or overwritten.
  echo Close any old CHECK or VERIFY windows, then rerun this same 0.4.7.1 updater.
) else (
  echo.
  echo Update completed successfully. Use the LOAD_MANIFEST path shown above.
)
pause
