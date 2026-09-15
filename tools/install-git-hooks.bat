@echo off
rem Installs tools\git-hooks\* into .git\hooks of this clone (hooks are not versioned by git itself).
rem pre-push: every git push builds dist\portable + dist\SysDeck-portable.zip + the installer.
cd /d "%~dp0.."
if not exist ".git\hooks" (
  echo [ERROR] .git\hooks not found - run from a git clone.
  exit /b 1
)
copy /y "tools\git-hooks\pre-push" ".git\hooks\pre-push" >nul
if errorlevel 1 exit /b 1
echo [OK] .git\hooks\pre-push installed
