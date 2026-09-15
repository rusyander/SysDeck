@echo off
rem Convenience shim: dev-run npm run dev
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev-run.ps1" %*
