Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports CaptureEngine.Configuration.Schema

Namespace CaptureEngine.Configuration
    ''' <summary>
    ''' Loads and saves EngineConfigV2 to/from a single JSON file
    ''' (default name: engine-config.v2.json).
    '''
    ''' JSON serialization options:
    '''   - PropertyNameCaseInsensitive = True (tolerant of camelCase/PascalCase)
    '''   - AllowTrailingCommas = True
    '''   - ReadCommentHandling = Skip (tolerant of // comments in config)
    '''   - DefaultIgnoreCondition = WhenWritingNull (no null fields in output)
    '''
    ''' On load:
    '''   - File missing → returns Nothing (caller decides whether to migrate from V1)
    '''   - File present but malformed → throws JsonException
    '''   - File present and valid → returns EngineConfigV2 instance
    ''' </summary>
    Public NotInheritable Class ConfigLoader
        Private Sub New()
            ' Static helper class — no instances.
        End Sub

        Public Const DefaultFileName As String = "engine-config.v2.json"

        Private Shared ReadOnly _jsonOpts As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True,
            .AllowTrailingCommas = True,
            .ReadCommentHandling = JsonCommentHandling.Skip,
            .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            .WriteIndented = True
        }

        ''' <summary>
        ''' After deserialization, normalize Runtime.Hotkeys to use the same
        ''' case-insensitive comparer as a fresh EngineConfigV2 instance.
        '''
        ''' STABILIZATION FIX (Phase 1.F):
        ''' System.Text.Json deserializes Dictionary(Of String, String) using the
        ''' default ordinal case-sensitive comparer (it does not honor the
        ''' JsonPropertyNameCaseInsensitive flag for dictionary KEYS — that flag
        ''' only affects property names). Without this normalization, a config
        ''' loaded from JSON would have a hotkeys dictionary that does NOT
        ''' match "ToggleOverlay" vs "toggleoverlay" case-insensitively, even
        ''' though a fresh New EngineConfigV2() instance does.
        '''
        ''' This fix rebuilds the dictionary with the same comparer as the
        ''' V2 constructor uses (StringComparer.OrdinalIgnoreCase).
        ''' </summary>
        Private Shared Sub NormalizeHotkeysComparer(cfg As EngineConfigV2)
            If cfg?.Runtime?.Hotkeys Is Nothing Then Return
            If cfg.Runtime.Hotkeys.Comparer Is StringComparer.OrdinalIgnoreCase Then Return

            Dim original As Dictionary(Of String, String) = cfg.Runtime.Hotkeys
            Dim normalized As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each kvp As KeyValuePair(Of String, String) In original
                ' If duplicate keys differ only by case, last one wins (deterministic).
                normalized(kvp.Key) = kvp.Value
            Next
            cfg.Runtime.Hotkeys = normalized
        End Sub

        ''' <summary>Load V2 config from a specific file path. Returns Nothing if file does not exist.</summary>
        Public Shared Function Load(filePath As String) As EngineConfigV2
            If String.IsNullOrEmpty(filePath) Then Return Nothing
            If Not File.Exists(filePath) Then Return Nothing

            Dim json As String = File.ReadAllText(filePath)
            If String.IsNullOrWhiteSpace(json) Then Return Nothing

            Dim cfg As EngineConfigV2 = JsonSerializer.Deserialize(Of EngineConfigV2)(json, _jsonOpts)
            If cfg Is Nothing Then Return Nothing

            ' Always normalize Version field after load (defensive against manual edits).
            If cfg.Version <> EngineConfigV2.SchemaVersion Then
                cfg.Version = EngineConfigV2.SchemaVersion
            End If

            ' Phase 1.F: normalize hotkeys dictionary comparer for case-insensitive lookup
            NormalizeHotkeysComparer(cfg)

            Return cfg
        End Function

        ''' <summary>Save V2 config to a specific file path.</summary>
        Public Shared Sub Save(cfg As EngineConfigV2, filePath As String)
            If cfg Is Nothing Then Throw New ArgumentNullException(NameOf(cfg))
            If String.IsNullOrEmpty(filePath) Then Throw New ArgumentException("filePath is empty.", NameOf(filePath))

            cfg.GeneratedAt = DateTime.UtcNow
            cfg.Version = EngineConfigV2.SchemaVersion

            Dim dir As String = Path.GetDirectoryName(filePath)
            If Not String.IsNullOrEmpty(dir) AndAlso Not Directory.Exists(dir) Then
                Directory.CreateDirectory(dir)
            End If

            Dim json As String = JsonSerializer.Serialize(cfg, _jsonOpts)
            File.WriteAllText(filePath, json)
        End Sub

        ''' <summary>Load V2 config from the default location next to the Engine binary.</summary>
        Public Shared Function LoadDefault(appBaseDir As String) As EngineConfigV2
            If String.IsNullOrEmpty(appBaseDir) Then
                appBaseDir = AppDomain.CurrentDomain.BaseDirectory
            End If
            ' FIX (BC30456 + BC42104): local variable was named 'path' which
            ' shadowed the System.IO.Path type. VB.NET name resolution then
            ' interpreted 'Path.Combine(...)' as a method call on the local
            ' String variable (which has no Combine method) → BC30456, and
            ' the local was used before assignment → BC42104.
            ' Renaming to 'filePath' (matching the Load/Save parameter names)
            ' restores System.IO.Path type resolution.
            Dim filePath As String = Path.Combine(appBaseDir, DefaultFileName)
            Return Load(filePath)
        End Function

        ''' <summary>Save V2 config to the default location next to the Engine binary.</summary>
        Public Shared Sub SaveDefault(cfg As EngineConfigV2, appBaseDir As String)
            If String.IsNullOrEmpty(appBaseDir) Then
                appBaseDir = AppDomain.CurrentDomain.BaseDirectory
            End If
            ' FIX (BC30456 + BC42104): same shadowing bug as LoadDefault above.
            Dim filePath As String = Path.Combine(appBaseDir, DefaultFileName)
            Save(cfg, filePath)
        End Sub

        ''' <summary>
        ''' Serialize V2 config to a JSON string. Useful for snapshot tests.
        ''' </summary>
        Public Shared Function SerializeToJson(cfg As EngineConfigV2) As String
            If cfg Is Nothing Then Throw New ArgumentNullException(NameOf(cfg))
            Return JsonSerializer.Serialize(cfg, _jsonOpts)
        End Function

        ''' <summary>
        ''' Deserialize V2 config from a JSON string. Useful for snapshot tests.
        ''' </summary>
        Public Shared Function DeserializeFromJson(json As String) As EngineConfigV2
            If String.IsNullOrEmpty(json) Then Throw New ArgumentException("json is empty.", NameOf(json))
            Dim cfg As EngineConfigV2 = JsonSerializer.Deserialize(Of EngineConfigV2)(json, _jsonOpts)
            NormalizeHotkeysComparer(cfg)
            Return cfg
        End Function
    End Class
End Namespace
