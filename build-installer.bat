@echo off
setlocal
rem ==== SysDeck - build both distributions ====
rem   <dist>\SysDeck-Setup.exe      installer with the app embedded as a resource
rem   <dist>\portable\                            exe + portable.marker + README.txt (recreated from scratch)
rem   <dist>\SysDeck-portable.zip   the same folder packed for handing over
rem <dist> = %SYSDECK_DIST% if set (the pre-push hook builds a clean copy of the pushed commit into the repo's dist),
rem otherwise dist\ next to this script. The tracked repo-root exe is not touched: the app is compiled into <dist>\build\.
rem Same rules as build.bat: only the csc.exe that ships with Windows, nothing to install.

cd /d "%~dp0"

set "DIST=%SYSDECK_DIST%"
if "%DIST%"=="" set "DIST=%~dp0dist"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe of .NET Framework 4.x not found.
  echo Expected: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  exit /b 1
)

rem ---- 1. the application itself ----
echo === Building the application ===
if not exist "%DIST%\build" mkdir "%DIST%\build"
set "APP=%DIST%\build\SysDeck.exe"
if exist "%APP%" del /f /q "%APP%"
call "%~dp0build.bat" "%APP%"
if errorlevel 1 (
  echo [ERROR] The application did not build.
  exit /b 1
)
if not exist "%APP%" (
  echo [ERROR] %APP% is missing after build.bat.
  exit /b 1
)

rem ---- 2. the installer ----
echo.
echo === Building the installer ===
if exist "%DIST%\SysDeck-Setup.exe" del /f /q "%DIST%\SysDeck-Setup.exe"
if exist "%DIST%\SysDeck-Setup.exe" (
  echo [ERROR] %DIST%\SysDeck-Setup.exe is locked - close the running installer.
  exit /b 1
)

set ICONOPT=
if exist "icon.ico" set ICONOPT=/win32icon:icon.ico

"%CSC%" /nologo /warn:4 /target:winexe /optimize+ "/out:%DIST%\SysDeck-Setup.exe" ^
  /win32manifest:installer\setup.manifest ^
  %ICONOPT% ^
  "/resource:%APP%,app.exe" ^
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

rem ---- 3. the portable layout: the old folder goes, the new one takes its place ----
echo.
echo === Laying out the portable build ===
if exist "%DIST%\portable" rmdir /s /q "%DIST%\portable"
if exist "%DIST%\portable" (
  echo [ERROR] %DIST%\portable could not be removed - close the portable copy running from it.
  exit /b 1
)
mkdir "%DIST%\portable"
copy /y "%APP%" "%DIST%\portable\SysDeck.exe" >nul
if errorlevel 1 exit /b 1
copy /y "installer\portable.marker" "%DIST%\portable\portable.marker" >nul
if errorlevel 1 exit /b 1
copy /y "installer\portable-readme.txt" "%DIST%\portable\README.txt" >nul
if errorlevel 1 exit /b 1

rem ---- 4. the same folder as one zip ----
if exist "%DIST%\SysDeck-portable.zip" del /f /q "%DIST%\SysDeck-portable.zip"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%DIST%\portable\*' -DestinationPath '%DIST%\SysDeck-portable.zip' -Force"
if errorlevel 1 (
  echo [ERROR] Could not pack the portable zip.
  exit /b 1
)
rmdir /s /q "%DIST%\build"

echo.
echo [OK] %DIST%\SysDeck-Setup.exe
echo [OK] %DIST%\portable\
echo [OK] %DIST%\SysDeck-portable.zip
endlocal
