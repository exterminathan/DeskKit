@echo off
rem Eye Saver: run alone, with no DeskKit shell. No arguments = "eyesaver toggle". Keep CRLF line endings.
setlocal
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0src\monitor.ps1" eyesaver toggle
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0src\monitor.ps1" %*
)
exit /b %errorlevel%
