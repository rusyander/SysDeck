@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tv.ps1" -Status -Watch 20
pause
