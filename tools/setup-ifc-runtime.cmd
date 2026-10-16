@echo off
setlocal
set "COORDINATOR_RUNTIME=%LOCALAPPDATA%\Cmcoordinator\Runtime"
py -3.14 -m venv "%COORDINATOR_RUNTIME%"
if errorlevel 1 exit /b 1
"%COORDINATOR_RUNTIME%\Scripts\python.exe" -m pip install --disable-pip-version-check -r "%~dp0local-live-viewer\ifc\requirements.txt"
exit /b %errorlevel%
