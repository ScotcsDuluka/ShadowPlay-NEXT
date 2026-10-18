' AppConfigShared.vb — SHARED config.json ACCESS FOR NON-OVERLAY PROCESSES
'
' config.json (NVIDIA Config\config.json) is THE single user-facing settings file.
' The full typed model lives in the Overlay project (AppSettings), but three
' other processes need to touch individual keys without owning the schema:
'
'   Launcher.exe  — writes Overlay.UseOverlayEnabled (toggle)
'   NVIDIA API (hub)              — reads  Overlay.UseOverlayEnabled every
'                                   second to start/keep-alive or kill the
'                                   overlay stack (Notifier/ShadowPlay/Capture)
'   NVIDIA Notifier               — reads  UI.Language for localized toasts
'
' This module gives them a tiny, dependency-free read/patch API. It NEVER
' rewrites the file from a cached model: every write is a parse of the
' CURRENT file content, a one-key mutation, and an immediate write-back, so
' sections owned by other processes survive untouched.
'
'   ReadBool / ReadString  — missing file / corrupt JSON / wrong type →
'                            the caller's fallback. Never creates the file.
'   WriteBool              — creates the file (and section) on demand via
'                            AppLayout.EnsureParentDir; never throws.
'
' This file is deliberately dependency-free (besides AppLayout) and
' Option Strict On-clean so it compiles identically inside every app
' project — same contract as AppLayout.vb.

Imports System
Imports System.Diagnostics
Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Nodes

Public Module AppConfigShared

    Private _configPath As String = Nothing

    ''' <summary>Path to the unified config file (NVIDIA Config\config.json).</summary>
    Public Function ConfigPath() As String
        If _configPath Is Nothing Then
            _configPath = AppLayout.P("NVIDIA Config", "config.json")
        End If
        SyncMirrors()
        Return _configPath
    End Function

    ' --- config mirrors: extra config.json copies kept in sync (e.g. the
    '     Forms overlay own NVIDIA Config folder) so every consumer sees the same
    '     settings regardless of which app wrote last. ---
    Private ReadOnly Mirrors As New List(Of String)()
    Private _mirrorSynced As Boolean

    Public Sub RegisterMirror(path As String)
        If Not String.IsNullOrEmpty(path) AndAlso Not Mirrors.Contains(path) Then
            Mirrors.Add(path)
            _mirrorSynced = False
        End If
    End Sub

    Private Sub SyncMirrors()
        If _mirrorSynced Then Return
        _mirrorSynced = True
        Try
            Dim primary As String = ConfigPath()
            Dim newest As String = primary
            Dim newestTime As DateTime = If(File.Exists(primary), File.GetLastWriteTime(primary), DateTime.MinValue)
            For Each m As String In Mirrors
                If File.Exists(m) Then
                    Dim t As DateTime = File.GetLastWriteTime(m)
                    If t > newestTime Then
                        newestTime = t
                        newest = m
                    End If
                End If
            Next
            If newest <> primary AndAlso File.Exists(newest) Then
                AppLayout.EnsureParentDir(primary)
                File.Copy(newest, primary, True)
            End If
            For Each m As String In Mirrors
                If File.Exists(primary) Then
                    AppLayout.EnsureParentDir(m)
                    File.Copy(primary, m, True)
                End If
            Next
        Catch
        End Try
    End Sub

    Public Sub MirrorNow()
        Try
            If Not File.Exists(ConfigPath()) Then Return
            For Each m As String In Mirrors
                AppLayout.EnsureParentDir(m)
                File.Copy(ConfigPath(), m, True)
            Next
        Catch
        End Try
    End Sub

    ''' <summary>
    ''' Reads a boolean key from [section]. Falls back when the file, the
    ''' section, or the key is missing — or when the value is not a boolean.
    ''' </summary>
    Public Function ReadBool(sectionName As String, keyName As String, fallback As Boolean) As Boolean
        Try
            Dim rootObj As JsonObject = LoadRootObject()
            If rootObj Is Nothing Then Return fallback

            Dim sectionObj As JsonObject = FindSection(rootObj, sectionName)
            If sectionObj Is Nothing Then Return fallback

            Dim valueNode As JsonNode = FindMember(sectionObj, keyName)
            If valueNode Is Nothing Then Return fallback

            Dim value As JsonValue = TryCast(valueNode, JsonValue)
            Dim result As Boolean = fallback
            If value IsNot Nothing AndAlso value.TryGetValue(Of Boolean)(result) Then
                Return result
            End If
            Return fallback
        Catch
            Return fallback
        End Try
    End Function

    ''' <summary>
    ''' Reads an integer key from [section]. Falls back when the file, the
    ''' section, or the key is missing — or when the value is not a number.
    ''' </summary>
    Public Function ReadInt(sectionName As String, keyName As String, fallback As Integer) As Integer
        Try
            Dim rootObj As JsonObject = LoadRootObject()
            If rootObj Is Nothing Then Return fallback

            Dim sectionObj As JsonObject = FindSection(rootObj, sectionName)
            If sectionObj Is Nothing Then Return fallback

            Dim valueNode As JsonNode = FindMember(sectionObj, keyName)
            If valueNode Is Nothing Then Return fallback

            Dim value As JsonValue = TryCast(valueNode, JsonValue)
            Dim result As Integer = fallback
            If value IsNot Nothing AndAlso value.TryGetValue(Of Integer)(result) Then
                Return result
            End If
            Return fallback
        Catch
            Return fallback
        End Try
    End Function

    ''' <summary>
    ''' Reads a string key from [section]. Falls back when the file, the
    ''' section, or the key is missing — or when the value is not a string.
    ''' </summary>
    Public Function ReadString(sectionName As String, keyName As String, fallback As String) As String
        Try
            Dim rootObj As JsonObject = LoadRootObject()
            If rootObj Is Nothing Then Return fallback

            Dim sectionObj As JsonObject = FindSection(rootObj, sectionName)
            If sectionObj Is Nothing Then Return fallback

            Dim valueNode As JsonNode = FindMember(sectionObj, keyName)
            If valueNode Is Nothing Then Return fallback

            Dim value As JsonValue = TryCast(valueNode, JsonValue)
            Dim result As String = fallback
            If value IsNot Nothing AndAlso value.TryGetValue(Of String)(result) Then
                Return result
            End If
            Return fallback
        Catch
            Return fallback
        End Try
    End Function

    ''' <summary>
    ''' Writes a boolean key into [section] using read-modify-write on the
    ''' CURRENT file content, so keys owned by other processes are preserved.
    ''' Creates the file and section on demand. Never throws.
    ''' </summary>
    Public Sub WriteBool(sectionName As String, keyName As String, value As Boolean)
        Try
            Dim targetPath As String = ConfigPath()
            AppLayout.EnsureParentDir(targetPath)

            Dim rootObj As JsonObject = Nothing
            If File.Exists(targetPath) Then
                Dim jsonText As String = File.ReadAllText(targetPath)
                If Not String.IsNullOrWhiteSpace(jsonText) Then
                    rootObj = TryCast(JsonNode.Parse(jsonText), JsonObject)
                End If
            End If
            If rootObj Is Nothing Then rootObj = New JsonObject()

            Dim sectionObj As JsonObject = TryCast(FindMember(rootObj, sectionName), JsonObject)
            If sectionObj Is Nothing Then
                sectionObj = New JsonObject()
                rootObj(sectionName) = sectionObj
            End If

            sectionObj(keyName) = value

            Dim options As New JsonSerializerOptions With {.WriteIndented = True}
            Dim finalJson As String = rootObj.ToJsonString(options)

            ' Atomic swap with a per-PID temp name: a crash mid-write can no
            ' longer leave a truncated config.json behind, and two processes
            ' saving at once can never interleave inside one shared temp
            ' file. The previous content is kept as .bak — same recovery
            ' contract as the Overlay's AppSettings.WriteConfigFileAtomic.
            Dim tmpPath As String = targetPath & "." & Process.GetCurrentProcess().Id.ToString() & ".tmp"
            Dim bakPath As String = targetPath & ".bak"
            File.WriteAllText(tmpPath, finalJson)
            If File.Exists(targetPath) Then
                File.Copy(targetPath, bakPath, True)
            End If
            File.Move(tmpPath, targetPath, True)
            MirrorNow()
        Catch
            ' config.json being locked/corrupt must never take the caller down
            ' (the Launcher tick and the API hub call this on a timer).
        End Try
    End Sub

    ''' <summary>
    ''' Writes a string key into [section] using the same atomic
    ''' read-modify-write contract as WriteBool.
    ''' </summary>
    Public Sub WriteString(sectionName As String, keyName As String, value As String)
        Try
            Dim targetPath As String = ConfigPath()
            AppLayout.EnsureParentDir(targetPath)

            Dim rootObj As JsonObject = Nothing
            If File.Exists(targetPath) Then
                Dim jsonText As String = File.ReadAllText(targetPath)
                If Not String.IsNullOrWhiteSpace(jsonText) Then
                    rootObj = TryCast(JsonNode.Parse(jsonText), JsonObject)
                End If
            End If
            If rootObj Is Nothing Then rootObj = New JsonObject()

            Dim sectionObj As JsonObject = TryCast(FindMember(rootObj, sectionName), JsonObject)
            If sectionObj Is Nothing Then
                sectionObj = New JsonObject()
                rootObj(sectionName) = sectionObj
            End If

            sectionObj(keyName) = If(value, String.Empty)

            Dim options As New JsonSerializerOptions With {.WriteIndented = True}
            Dim finalJson As String = rootObj.ToJsonString(options)
            Dim tmpPath As String = targetPath & "." & Process.GetCurrentProcess().Id.ToString() & ".tmp"
            Dim bakPath As String = targetPath & ".bak"
            File.WriteAllText(tmpPath, finalJson)
            If File.Exists(targetPath) Then File.Copy(targetPath, bakPath, True)
            File.Move(tmpPath, targetPath, True)
            MirrorNow()
        Catch
            ' Settings persistence must not take down the controller server.
        End Try
    End Sub

    ''' <summary>
    ''' Loads and parses the CURRENT config.json content. Returns Nothing when
    ''' the file is missing, empty, or not a JSON object — callers fall back.
    ''' </summary>
    Private Function LoadRootObject() As JsonObject
        Try
            If Not File.Exists(ConfigPath()) Then Return Nothing
            Dim jsonText As String = File.ReadAllText(ConfigPath())
            If String.IsNullOrWhiteSpace(jsonText) Then Return Nothing
            Return TryCast(JsonNode.Parse(jsonText), JsonObject)
        Catch
            Return Nothing
        End Try
    End Function

    ''' <summary>Finds [section] at the root, tolerating casing differences.</summary>
    Private Function FindSection(rootObj As JsonObject, sectionName As String) As JsonObject
        Return TryCast(FindMember(rootObj, sectionName), JsonObject)
    End Function

    ''' <summary>Case-insensitive member lookup on a JSON object
    ''' (System.Text.Json node indexing is case-sensitive; the Overlay's
    ''' typed model deserializes case-insensitively, so hand-edited or
    ''' re-cased files must still resolve).</summary>
    Private Function FindMember(obj As JsonObject, memberName As String) As JsonNode
        For Each kvp As KeyValuePair(Of String, JsonNode) In obj
            If String.Equals(kvp.Key, memberName, StringComparison.OrdinalIgnoreCase) Then
                Return kvp.Value
            End If
        Next
        Return Nothing
    End Function

End Module
