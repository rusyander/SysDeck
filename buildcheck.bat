@echo off
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
for %%I in ("%CSC%") do set FW=%%~dpI
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
"%CSC%" /nologo /target:winexe /optimize+ /warn:4 /out:"%TEMP%\wpc-check.exe" /win32manifest:app.manifest %EXTRES% /reference:System.dll /reference:System.Core.dll /reference:System.Numerics.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll /reference:"%FW%System.Runtime.dll" /reference:"%FW%System.Runtime.WindowsRuntime.dll" /reference:"%FW%System.Runtime.InteropServices.WindowsRuntime.dll" /reference:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /reference:"%WINDIR%\System32\WinMetadata\Windows.Graphics.winmd" src\*.cs
