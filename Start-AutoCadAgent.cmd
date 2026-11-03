@echo off
setlocal
cd /d "%~dp0"
set "CADAGENT_APP=%~dp0AutoCadAgent.Tui.exe"
if not exist "%CADAGENT_APP%" set "CADAGENT_APP=%~dp0artifacts\app\AutoCadAgent.Tui.exe"
if not exist "%CADAGENT_APP%" (
  echo Build first: powershell -NoProfile -File scripts\build.ps1
  pause
  exit /b 1
)
start "" "%CADAGENT_APP%" --workspace "%~dp0workspace"
