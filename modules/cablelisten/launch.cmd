@echo off
rem Cable Listen: run alone, with no DeskKit shell. Keep CRLF line endings.
setlocal
set "ENTRY=%~dp0src\cable_listen.pyw"
set "PYMIN=3.12"
where pyw >nul 2>nul || goto :trypython
start "" pyw -3 "%ENTRY%" %*
exit /b 0
:trypython
where pythonw >nul 2>nul || goto :nopython
start "" pythonw "%ENTRY%" %*
exit /b 0
:nopython
echo Python %PYMIN% or newer not found. Install it from https://www.python.org/downloads/windows/ and try again.
pause
exit /b 4
