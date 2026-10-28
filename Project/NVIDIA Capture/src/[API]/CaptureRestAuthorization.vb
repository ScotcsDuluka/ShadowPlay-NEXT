Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text

' The Container and worker share the existing CurrentUser DPAPI pairing vault.
' This reader never enrolls, rotates, logs or falls back to plaintext settings.
Public NotInheritable Class CaptureRestAuthorization
    Public Const TokenHeader As String = "x-shadowplay-capture-token"
    Private Sub New()
    End Sub

    <StructLayout(LayoutKind.Sequential)>
    Private Structure DataBlob
        Public Size As Integer
        Public Data As IntPtr
    End Structure

    <DllImport("crypt32.dll", SetLastError:=True)>
    Private Shared Function CryptUnprotectData(ByRef source As DataBlob, description As IntPtr,
        entropy As IntPtr, reserved As IntPtr, prompt As IntPtr, flags As Integer,
        ByRef output As DataBlob) As <MarshalAs(UnmanagedType.Bool)> Boolean
    End Function

    <DllImport("kernel32.dll")>
    Private Shared Function LocalFree(memory As IntPtr) As IntPtr
    End Function

    Public Shared ReadOnly Property ProtectedTokenPath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "ShadowPlay NEXT", "Secrets", "pairing-current-user.dpapi")
        End Get
    End Property

    Public Shared Function ReadProtectedToken() As String
        Dim input As IntPtr = IntPtr.Zero
        Dim clear As New DataBlob()
        Dim clearBytes As Byte() = Nothing
        Try
            Dim info As New FileInfo(ProtectedTokenPath)
            If Not info.Exists OrElse info.Length < 1 OrElse info.Length > 4096 Then Return ""
            Dim encrypted = File.ReadAllBytes(info.FullName)
            If encrypted.Length < 1 OrElse encrypted.Length > 4096 Then Return ""
            input = Marshal.AllocHGlobal(encrypted.Length)
            Marshal.Copy(encrypted, 0, input, encrypted.Length)
            Dim blob As New DataBlob With {.Size = encrypted.Length, .Data = input}
            If Not CryptUnprotectData(blob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                      IntPtr.Zero, 1, clear) Then Return ""
            If clear.Size <> 32 Then Return ""
            clearBytes = New Byte(clear.Size - 1) {}
            Marshal.Copy(clear.Data, clearBytes, 0, clearBytes.Length)
            Dim token = Encoding.ASCII.GetString(clearBytes)
            Return If(ValidToken(token), token, "")
        Catch
            Return ""
        Finally
            If clearBytes IsNot Nothing Then CryptographicOperations.ZeroMemory(clearBytes)
            If clear.Data <> IntPtr.Zero Then
                If clear.Size > 0 AndAlso clear.Size <= 4096 Then
                    For index As Integer = 0 To clear.Size - 1
                        Marshal.WriteByte(clear.Data, index, 0)
                    Next
                End If
                LocalFree(clear.Data)
            End If
            If input <> IntPtr.Zero Then Marshal.FreeHGlobal(input)
        End Try
    End Function

    Public Shared Function ValidToken(value As String) As Boolean
        If value Is Nothing OrElse value.Length <> 32 Then Return False
        For Each digit As Char In value
            If Not Uri.IsHexDigit(digit) Then Return False
        Next
        Return True
    End Function

    ' Returns the HTTP status; zero means authorized. No state mutation occurs.
    Public Shared Function Authorize(method As String, request As IReadOnlyDictionary(Of String, String),
                                     port As Integer, expectedToken As String) As Integer
        Dim host As String = ""
        If Not request.TryGetValue("header:host", host) OrElse Not IsLocalHost(host, port) Then Return 403
        Dim origin As String = ""
        If request.TryGetValue("header:origin", origin) Then
            Dim originUri As Uri = Nothing
            If Not Uri.TryCreate(origin, UriKind.Absolute, originUri) OrElse
               originUri.Scheme <> "http" OrElse originUri.UserInfo.Length <> 0 OrElse
               originUri.AbsolutePath <> "/" OrElse originUri.Query.Length <> 0 OrElse
               originUri.Fragment.Length <> 0 OrElse
               Not String.Equals(originUri.Authority, host, StringComparison.OrdinalIgnoreCase) Then Return 403
        End If
        If method = "GET" Then Return 0
        If method <> "POST" Then Return 405
        If Not ValidToken(expectedToken) Then Return 503
        Dim supplied As String = ""
        If Not request.TryGetValue("header:" & TokenHeader, supplied) OrElse Not ValidToken(supplied) Then Return 403
        Dim expectedBytes = Encoding.ASCII.GetBytes(expectedToken)
        Dim suppliedBytes = Encoding.ASCII.GetBytes(supplied)
        Try
            Return If(CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes), 0, 403)
        Finally
            CryptographicOperations.ZeroMemory(expectedBytes)
            CryptographicOperations.ZeroMemory(suppliedBytes)
        End Try
    End Function

    Private Shared Function IsLocalHost(host As String, port As Integer) As Boolean
        Return String.Equals(host, "127.0.0.1:" & port, StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(host, "localhost:" & port, StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(host, "[::1]:" & port, StringComparison.OrdinalIgnoreCase)
    End Function
End Class
