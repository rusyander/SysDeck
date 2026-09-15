@echo off
rem Writes the csc.exe response file with every embedded resource: resources.bat <file.rsp>
rem   extension\ (minus extension\test\) -> "extension/<path>": unpacked for "Load unpacked" in the browser;
rem   toolkit\                            -> "toolkit/<path>":   scripts deployed by the "Scripts" page.
rem A response file instead of a variable: the command line of cmd.exe stops at 8191 characters.
setlocal EnableDelayedExpansion
for %%R in ("%~dp0.") do set "ABSROOT=%%~fR\"
set "RSP=%~1"
type nul > "%RSP%"
if exist "%ABSROOT%extension" for /r "%ABSROOT%extension" %%F in (*) do (
  set "REL=%%F"
  set "REL=!REL:%ABSROOT%=!"
  if /i not "!REL:~0,15!"=="extension\test\" echo "/resource:%%F,!REL:\=/!">> "%RSP%"
)
if exist "%ABSROOT%toolkit" for /r "%ABSROOT%toolkit" %%F in (*) do (
  set "REL=%%F"
  set "REL=!REL:%ABSROOT%=!"
  echo "/resource:%%F,!REL:\=/!">> "%RSP%"
)
endlocal
