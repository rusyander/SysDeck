@echo off
setlocal
rem ==== SysDeck - run the test suite ====
rem Compiles src\*.cs together with tests\*.cs into a TEMPORARY output and runs it.
rem The repository binary SysDeck.exe is never written to.
rem No installation required: .NET Framework ships with Windows.

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe of .NET Framework 4.x not found.
  echo Expected: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
  exit /b 1
)

set ROOT=%~dp0..
set OUTDIR=%TEMP%\wpc-test-build
set OUT=%OUTDIR%\WpcTests.exe
if not exist "%OUTDIR%" mkdir "%OUTDIR%"

echo Compiler: %CSC%
rem Capture video (Windows.Graphics.Capture) needs the WinRT facades of the same framework and the
rem winmd files that ship in System32 on Windows 10/11.
for %%I in ("%CSC%") do set FW=%%~dpI
set WINMD=%WINDIR%\System32\WinMetadata
rem Embedded resources (browser extension, toolkit scripts) are listed in a csc response file by resources.bat:
rem a variable would hit the 8191-character limit of the cmd.exe command line.
set "RSP=%TEMP%\sysdeck-tests.rsp"
call "%ROOT%\resources.bat" "%RSP%"
echo Building the test suite ...

rem No /win32manifest on purpose: a test run may never depend on a UAC prompt. The app itself
rem runs asInvoker and raises rights per operation, so the suite covers that split without one.
rem SYSDECK_TEST_APP points the elevation tests at the built app; without it they are skipped.
set SYSDECK_TEST_APP=%ROOT%\SysDeck.exe
rem Независимая сверка видео: ffprobe читает собранный файл чужим кодом. Нет его — эти проверки честно пропускаются,
rem но молча отключать сверку нельзя, поэтому ищем его в PATH сами.
if "%SYSDECK_FFPROBE%"=="" for /f "delims=" %%P in ('where ffprobe 2^>nul') do if "%SYSDECK_FFPROBE%"=="" set "SYSDECK_FFPROBE=%%P"
"%CSC%" /nologo /target:exe /warn:4 /main:SysDeck.Tests.TestMain /out:"%OUT%" "@%RSP%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Numerics.dll ^
  /reference:System.Xml.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Runtime.Serialization.dll ^
  /reference:"%FW%System.Runtime.dll" ^
  /reference:"%FW%System.Runtime.WindowsRuntime.dll" ^
  /reference:"%FW%System.Runtime.InteropServices.WindowsRuntime.dll" ^
  /reference:"%WINMD%\Windows.Foundation.winmd" ^
  /reference:"%WINMD%\Windows.Graphics.winmd" ^
  "%ROOT%\src\*.cs" "%ROOT%\tests\*.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  exit /b 1
)

echo.
rem Optional area names limit the run: run-tests.bat downloads capture
"%OUT%" %*
set RC=%ERRORLEVEL%
echo.
if "%RC%"=="0" (
  echo [OK] all tests passed
) else (
  echo [FAILED] %RC% assertion^(s^) failed
)
exit /b %RC%
