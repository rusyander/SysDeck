@echo off
setlocal
rem ==== Build (if needed) and run in one command ====
cd /d "%~dp0"

if not exist "SysDeck.exe" (
  call "%~dp0build.bat"
  if errorlevel 1 exit /b 1
)

start "" "SysDeck.exe"
endlocal
