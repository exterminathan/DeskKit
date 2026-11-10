@echo off
rem Hello (Python): run alone, with no DeskKit shell. Keep CRLF line endings.
rem Foreground (oneshot) module: runs in this console and returns its exit code.
setlocal
set "ENTRY=%~dp0src\hello.py"
set "PYMIN=3.11"
where py >nul 2>nul || goto :trypython
py -3 "%ENTRY%" %*
exit /b %errorlevel%
:trypython
where python >nul 2>nul || goto :nopython
python "%ENTRY%" %*
exit /b %errorlevel%
:nopython
echo Python %PYMIN% or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.
pause
exit /b 4
