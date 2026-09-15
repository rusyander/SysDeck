@echo off
setlocal
rem ==== SysDeck - build with the built-in Windows csc.exe (all src\*.cs) ====
rem No installation required: .NET Framework ships with Windows.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe of .NET Framework 4.x not found.
  echo Expected: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  exit /b 1
)

echo Compiler: %CSC%
rem Capture video (Windows.Graphics.Capture) needs the WinRT facades of the same framework and the
rem winmd files that ship in System32 on Windows 10/11.
for %%I in ("%CSC%") do set FW=%%~dpI
set WINMD=%WINDIR%\System32\WinMetadata
rem Optional %1 = output exe path (build-installer.bat builds dist without touching the tracked repo-root exe).
set "OUT=%~1"
if "%OUT%"=="" set "OUT=SysDeck.exe"
echo Building %OUT% ...

rem Embedded resources (browser extension, toolkit scripts) are listed in a csc response file by resources.bat:
rem a variable would hit the 8191-character limit of the cmd.exe command line.
set "RSP=%TEMP%\sysdeck-build.rsp"
call "%~dp0resources.bat" "%RSP%"

set ICONOPT=
if exist "icon.ico" set ICONOPT=/win32icon:icon.ico

"%CSC%" /nologo /target:winexe /optimize+ "/out:%OUT%" ^
  /win32manifest:app.manifest ^
  %ICONOPT% "@%RSP%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Numerics.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Runtime.Serialization.dll ^
  /reference:"%FW%System.Runtime.dll" ^
  /reference:"%FW%System.Runtime.WindowsRuntime.dll" ^
  /reference:"%FW%System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /reference:"%WINMD%\Windows.Foundation.winmd" ^
  /reference:"%WINMD%\Windows.Graphics.winmd" ^
  src\*.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  exit /b 1
)

echo.
echo [OK] Done: %OUT%
endlocal
