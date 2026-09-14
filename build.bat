@echo off
setlocal
rem ==== Windows Process Cleaner - build with the built-in Windows csc.exe (all src\*.cs) ====
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
echo Building WindowsProcessCleaner.exe ...

rem Browser extension (extension\ minus extension\test\) is embedded as resources "extension/<path>":
rem the app unpacks it for "Load unpacked". Relative names are built from the normalised repo root.
for %%R in ("%~dp0.") do set "ABSROOT=%%~fR\"
set EXTRES=
setlocal EnableDelayedExpansion
if exist "%ABSROOT%extension" for /r "%ABSROOT%extension" %%F in (*) do (
  set "REL=%%F"
  set "REL=!REL:%ABSROOT%=!"
  if /i not "!REL:~0,15!"=="extension\test\" set EXTRES=!EXTRES! "/resource:%%F,!REL:\=/!"
)
endlocal & set EXTRES=%EXTRES%

set ICONOPT=
if exist "icon.ico" set ICONOPT=/win32icon:icon.ico

"%CSC%" /nologo /target:winexe /optimize+ /out:WindowsProcessCleaner.exe ^
  /win32manifest:app.manifest ^
  %ICONOPT% %EXTRES% ^
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
echo [OK] Done: %CD%\WindowsProcessCleaner.exe
endlocal
