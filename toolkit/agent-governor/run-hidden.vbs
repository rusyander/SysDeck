' No-window launcher for PowerShell scripts run from Task Scheduler.
'
' `powershell.exe -WindowStyle Hidden` still creates a console frame and hides it
' a moment later - visible as a flash, and enough to steal focus from a
' fullscreen game. WScript.Shell.Run with window style 0 never creates one.
'
' Usage: wscript.exe run-hidden.vbs <script.ps1> [args...]
' A relative <script.ps1> resolves against this file's own folder.

Option Explicit

Dim shell, fso, here, target, cmd, i, a

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

If WScript.Arguments.Count = 0 Then WScript.Quit 1

here = fso.GetParentFolderName(WScript.ScriptFullName)
target = WScript.Arguments(0)

' Mid(target, 2, 1) = ":" catches C:\..., Left = "\\" catches UNC paths.
If Not (Mid(target, 2, 1) = ":" Or Left(target, 2) = "\\") Then
    target = fso.BuildPath(here, target)
End If

cmd = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File """ & target & """"

For i = 1 To WScript.Arguments.Count - 1
    a = WScript.Arguments(i)
    If InStr(a, " ") > 0 Then
        cmd = cmd & " """ & a & """"
    Else
        cmd = cmd & " " & a
    End If
Next

' 0 = hidden window, False = do not wait for it to finish.
shell.Run cmd, 0, False
