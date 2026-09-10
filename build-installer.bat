@echo off
setlocal
rem ==== Windows Process Cleaner - build both distributions into dist\ ====
rem   dist\WindowsProcessCleaner-Setup.exe  installer with the app embedded as a resource
rem   dist\portable\                        exe + portable.marker + README.txt
rem Same rules as build.bat: only the csc.exe that ships with Windows, nothing to install.

cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe of .NET Framework 4.x not found.
  echo Expected: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  exit /b 1
)

rem ---- 1. the application itself ----
echo === Building the application ===
call "%~dp0build.bat"
if errorlevel 1 (
  echo [ERROR] The application did not build. If it is running, close it first:
  echo         the exe is locked while an instance is up.
  exit /b 1
)
if not exist "WindowsProcessCleaner.exe" (
  echo [ERROR] WindowsProcessCleaner.exe is missing after build.bat.
  exit /b 1
)

rem ---- 2. the installer ----
echo.
echo === Building the installer ===
if not exist "dist" mkdir "dist"
if exist "dist\WindowsProcessCleaner-Setup.exe" del /f /q "dist\WindowsProcessCleaner-Setup.exe"

set ICONOPT=
if exist "icon.ico" set ICONOPT=/win32icon:icon.ico

"%CSC%" /nologo /warn:4 /target:winexe /optimize+ /out:dist\WindowsProcessCleaner-Setup.exe ^
  /win32manifest:installer\setup.manifest ^
  %ICONOPT% ^
  /resource:WindowsProcessCleaner.exe,app.exe ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  installer\*.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Installer build failed.
  exit /b 1
)

rem ---- 3. the portable layout ----
echo.
echo === Laying out the portable build ===
if not exist "dist\portable" mkdir "dist\portable"
copy /y "WindowsProcessCleaner.exe" "dist\portable\WindowsProcessCleaner.exe" >nul
if errorlevel 1 exit /b 1
copy /y "installer\portable.marker" "dist\portable\portable.marker" >nul
if errorlevel 1 exit /b 1
copy /y "installer\portable-readme.txt" "dist\portable\README.txt" >nul
if errorlevel 1 exit /b 1

echo.
echo [OK] %CD%\dist\WindowsProcessCleaner-Setup.exe
echo [OK] %CD%\dist\portable\
endlocal
