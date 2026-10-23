Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.IO

' Explicit opt-in only. Production starts exactly as before.
' A sandbox worker needs a dedicated root and a unique loopback REST port.
Public NotInheritable Class CaptureIsolation
    Private Sub New()
    End Sub

    Public Shared ReadOnly Property Requested As Boolean
        Get
            Return Environment.GetCommandLineArgs().Any(
                Function(arg) String.Equals(arg, "--capture-sandbox", StringComparison.OrdinalIgnoreCase))
        End Get
    End Property

    Public Shared ReadOnly Property Root As String
        Get
            Return If(Environment.GetEnvironmentVariable("NV_CAPTURE_ISOLATED_ROOT"), "").Trim()
        End Get
    End Property

    Public Shared ReadOnly Property ApiPort As Integer
        Get
            If Not Requested Then Return 59077
            Dim port As Integer
            If Integer.TryParse(Environment.GetEnvironmentVariable("NV_CAPTURE_ISOLATED_REST_PORT"), port) Then
                Return port
            End If
            Return -1
        End Get
    End Property

    Public Shared Function Validate(rootPath As String, appLayoutRoot As String,
                                    sandboxPort As Integer) As String
        If String.IsNullOrWhiteSpace(rootPath) OrElse Not Directory.Exists(rootPath) Then
            Return "isolated_root_missing"
        End If
        Dim canonical As String
        Try
            canonical = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar)
            Dim actual = Path.GetFullPath(appLayoutRoot).TrimEnd(Path.DirectorySeparatorChar)
            If Not String.Equals(canonical, actual, StringComparison.OrdinalIgnoreCase) Then
                Return "isolated_layout_root_mismatch"
            End If
            Dim marker = Path.Combine(canonical, ".shadowplay-capture-isolated")
            If Not File.Exists(marker) OrElse
               File.ReadAllText(marker).Trim() <> "shadowplay-next-isolated-capture-v1" Then
                Return "isolated_marker_missing"
            End If
            Dim configDir = Path.Combine(canonical, "Config")
            If Not File.Exists(Path.Combine(configDir, "config.json")) OrElse
               Not File.Exists(Path.Combine(configDir, "engine.json")) Then
                Return "isolated_config_missing"
            End If
            If sandboxPort < 59100 OrElse sandboxPort > 59999 Then
                Return "isolated_port_invalid"
            End If
        Catch
            Return "isolated_root_invalid"
        End Try
        Return ""
    End Function

    Public Shared Sub AssertReady(appLayoutRoot As String)
        If Not Requested Then Return
        Dim reason = Validate(Root, appLayoutRoot, ApiPort)
        If reason.Length > 0 Then
            Throw New InvalidOperationException("capture_sandbox_preflight_failed:" & reason)
        End If
    End Sub

    Public Shared Function RecordingDirectory(appLayoutRoot As String) As String
        If Not Requested Then Return ""
        AssertReady(appLayoutRoot)
        Dim destination = Path.Combine(Path.GetFullPath(Root), "Records")
        Directory.CreateDirectory(destination)
        Return destination
    End Function
End Class
