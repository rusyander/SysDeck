@echo off
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
for %%I in ("%CSC%") do set FW=%%~dpI
rem Embedded resources (browser extension, toolkit scripts) are listed in a csc response file by resources.bat:
rem a variable would hit the 8191-character limit of the cmd.exe command line.
set "RSP=%TEMP%\sysdeck-check.rsp"
call "%~dp0resources.bat" "%RSP%"
"%CSC%" /nologo /target:winexe /optimize+ /warn:4 /out:"%TEMP%\wpc-check.exe" /win32manifest:app.manifest "@%RSP%" /reference:System.dll /reference:System.Core.dll /reference:System.Numerics.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Runtime.Serialization.dll /reference:"%FW%System.Runtime.dll" /reference:"%FW%System.Runtime.WindowsRuntime.dll" /reference:"%FW%System.Runtime.InteropServices.WindowsRuntime.dll" /reference:"%WINDIR%\System32\WinMetadata\Windows.Foundation.winmd" /reference:"%WINDIR%\System32\WinMetadata\Windows.Graphics.winmd" src\*.cs
