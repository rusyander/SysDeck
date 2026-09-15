@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tv.ps1" -On
timeout /t 4 >nul
