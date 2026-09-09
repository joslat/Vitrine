@REM SPDX-License-Identifier: MIT
@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0start.ps1" %*
if errorlevel 1 (
  echo.
  echo VITRINE stopped because a command or evaluation gate failed.
  exit /b 1
)
endlocal
