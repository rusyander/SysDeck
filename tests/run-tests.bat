@echo off
setlocal
rem ==== Windows Process Cleaner - run the test suite ====
rem Compiles src\*.cs together with tests\*.cs into a TEMPORARY output and runs it.
rem The repository binary WindowsProcessCleaner.exe is never written to.
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
echo Building the test suite ...

rem No /win32manifest on purpose: a test run may never depend on a UAC prompt. The app itself
rem runs asInvoker and raises rights per operation, so the suite covers that split without one.
rem WPC_TEST_APP points the elevation tests at the built app; without it they are skipped.
set WPC_TEST_APP=%ROOT%\WindowsProcessCleaner.exe
"%CSC%" /nologo /target:exe /warn:4 /main:WindowsProcessCleaner.Tests.TestMain /out:"%OUT%" ^
  /reference:System.dll ^
  /reference:System.Core.dll ^
  /reference:System.Xml.dll ^
  /reference:System.Drawing.dll ^
  /reference:System.Windows.Forms.dll ^
  /reference:System.Runtime.Serialization.dll ^
  "%ROOT%\src\*.cs" "%ROOT%\tests\*.cs"

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed.
  exit /b 1
)

echo.
"%OUT%"
set RC=%ERRORLEVEL%
echo.
if "%RC%"=="0" (
  echo [OK] all tests passed
) else (
  echo [FAILED] %RC% assertion^(s^) failed
)
exit /b %RC%
