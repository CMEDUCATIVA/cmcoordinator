@echo off
setlocal
cd /d "%~dp0"
if not exist "node_modules\three\package.json" (
  echo Falta Three.js. Ejecuta npm.cmd ci en esta carpeta.
  exit /b 1
)
call npm.cmd start
