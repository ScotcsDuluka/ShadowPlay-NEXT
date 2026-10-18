Imports System
Imports System.IO
Imports System.Text

' Console + file logger (ContainerLog pattern). File:
' Logs\NVIDIA Web Helper.log beside the exe — i.e. Logs\ inside the
' deployed NodeAPI tree. Child (Node backend) lines are tagged
' [NODE]/[NODE!] so host and backend streams stay distinguishable in the
' same file (goal: stdout/stderr/logging ชัด).
Friend Module HostLog

    Private ReadOnly _lock As New Object()
    Private _path As String = ""

    Public Sub Init(path As String)
        SyncLock _lock
            _path = path
            Try
                File.AppendAllText(_path,
                    "==== NVIDIA Web Helper session " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & " ====" & vbCrLf,
                    Encoding.UTF8)
            Catch
            End Try
        End SyncLock
    End Sub

    Public Sub Log(msg As String)
        Write("INFO", msg)
    End Sub

    Public Sub Warn(msg As String)
        Write("WARN", msg)
    End Sub

    Public Sub [Error](msg As String)
        Write("ERROR", msg)
    End Sub

    Public Sub NodeOut(line As String)
        Write("NODE", line)
    End Sub

    Public Sub NodeErr(line As String)
        Write("NODE!", line)
    End Sub

    Private Sub Write(level As String, msg As String)
        Dim line As String = "[" & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") & "] [" & level & "] " & msg
        SyncLock _lock
            Console.WriteLine(line)
            If _path <> "" Then
                Try
                    File.AppendAllText(_path, line & vbCrLf, Encoding.UTF8)
                Catch
                End Try
            End If
        End SyncLock
    End Sub

End Module
