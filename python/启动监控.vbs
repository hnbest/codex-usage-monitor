' Codex Usage Monitor launcher - double click to start (no console window)
Option Explicit

Dim fso, sh, scriptDir, targetPy, pyw, fallbackPyw
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
targetPy = fso.BuildPath(scriptDir, "codex_tray.py")

If Not fso.FileExists(targetPy) Then
    MsgBox "codex_tray.py not found:" & vbCrLf & targetPy, vbCritical, "Codex Usage Monitor"
    WScript.Quit 1
End If

fallbackPyw = sh.ExpandEnvironmentStrings("%LOCALAPPDATA%\Programs\Python\Python39\pythonw.exe")
pyw = "pythonw.exe"

On Error Resume Next
sh.Run """" & pyw & """ """ & targetPy & """", 0, False
If Err.Number <> 0 Then
    Err.Clear
    If fso.FileExists(fallbackPyw) Then
        sh.Run """" & fallbackPyw & """ """ & targetPy & """", 0, False
    End If
    If Err.Number <> 0 Then
        MsgBox "Failed to start: pythonw.exe not found. Please install Python 3 and add it to PATH.", vbCritical, "Codex Usage Monitor"
        WScript.Quit 1
    End If
End If
On Error GoTo 0
