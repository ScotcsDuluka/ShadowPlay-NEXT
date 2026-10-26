Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab

' Read-only DXGI enumeration: NO D3D11 device, DuplicateOutput, or pixels.
' Capture owns one persistent primary-display selection for every workflow.
Public NotInheritable Class CaptureDisplayCatalog
    Private Sub New()
    End Sub

    Public NotInheritable Class Display
        Public Property OutputIndex As Integer
        Public Property AdapterLuidLow As UInteger
        Public Property AdapterLuidHigh As Integer
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property Left As Integer
        Public Property Top As Integer
        Public Property Primary As Boolean
        Public Property DeviceName As String = ""
        Public Property Label As String = ""
        Public Property VendorId As UInteger
        Public Property DeviceId As UInteger
        Public Property Vendor As String = ""
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property Target As CaptureTarget
    End Class

    Public NotInheritable Class Choice
        Public Property OutputIndex As Integer
        Public Property AdapterLuidLow As UInteger
        Public Property AdapterLuidHigh As Integer
        Public Property MonitorDevicePath As String = ""
    End Class

    Public Shared Function Enumerate() As List(Of Display)
        Dim found As New List(Of Display)()
        For Each target In DxgiDisplayInventory.Collect().Outputs.Where(Function(t) t.Eligible)
            found.Add(New Display With {
                .OutputIndex = target.OutputIndex, .AdapterLuidLow = target.Adapter.Low, .AdapterLuidHigh = target.Adapter.High,
                .DeviceName = target.DeviceName, .Width = target.Width, .Height = target.Height,
                .Left = target.Left, .Top = target.Top, .Primary = (target.Left = 0 AndAlso target.Top = 0),
                .VendorId = target.Adapter.VendorId, .DeviceId = target.Adapter.DeviceId, .Vendor = target.Adapter.Vendor.ToString(),
                .Target = target, .Label = $"Display {found.Count + 1} - {target.Width}x{target.Height} ({target.Adapter.Vendor})"})
        Next
        Return found
    End Function

    Public Shared Function SelectionPath(root As String) As String
        Return Path.Combine(root, "Config", "capture-display.json")
    End Function

    Public Shared Function LoadSelection(root As String) As Choice
        Dim selectionFile = SelectionPath(root)
        If Not IO.File.Exists(selectionFile) Then Return Nothing
        Using doc = JsonDocument.Parse(IO.File.ReadAllText(selectionFile))
            Dim json = doc.RootElement
            If json.ValueKind <> JsonValueKind.Object Then Throw New InvalidDataException("invalid_display_selection")
            Dim version As JsonElement = Nothing
            Dim outputIndex As JsonElement = Nothing
            Dim low As JsonElement = Nothing
            Dim high As JsonElement = Nothing
            If Not json.TryGetProperty("schemaVersion", version) OrElse
               version.ValueKind <> JsonValueKind.Number OrElse (version.GetInt32() <> 1 AndAlso version.GetInt32() <> 2) OrElse
               Not json.TryGetProperty("outputIndex", outputIndex) OrElse
               Not json.TryGetProperty("adapterLuidLow", low) OrElse
               Not json.TryGetProperty("adapterLuidHigh", high) Then
                Throw New InvalidDataException("invalid_display_selection")
            End If
            Dim monitorPath As String = ""
            If version.GetInt32() = 2 Then
                Dim monitor As JsonElement
                If Not json.TryGetProperty("monitorDevicePath", monitor) OrElse monitor.ValueKind <> JsonValueKind.String Then
                    Throw New InvalidDataException("invalid_display_selection")
                End If
                monitorPath = monitor.GetString()
                If String.IsNullOrWhiteSpace(monitorPath) Then Throw New InvalidDataException("invalid_display_selection")
            End If
            Return New Choice With {
                .OutputIndex = outputIndex.GetInt32(),
                .AdapterLuidLow = low.GetUInt32(),
                .AdapterLuidHigh = high.GetInt32(), .MonitorDevicePath = monitorPath
            }
        End Using
    End Function

    ' Never silently fall back to another output after a monitor is unplugged.
    Public Shared Function Resolve(root As String, displays As IReadOnlyList(Of Display)) As Display
        If displays Is Nothing OrElse displays.Count = 0 Then
            Throw New InvalidOperationException("capture_display_unavailable")
        End If
        Dim saved = LoadSelection(root)
        If saved Is Nothing Then Return displays(0) ' Backward-compatible first attached.
        Dim match = displays.FirstOrDefault(Function(d) d.OutputIndex = saved.OutputIndex AndAlso
                                            d.AdapterLuidLow = saved.AdapterLuidLow AndAlso
                                            d.AdapterLuidHigh = saved.AdapterLuidHigh)
        If match Is Nothing Then Throw New InvalidOperationException("capture_display_disconnected")
        If saved.MonitorDevicePath.Length > 0 AndAlso (match.Target Is Nothing OrElse Not String.Equals(
            saved.MonitorDevicePath, match.Target.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)) Then
            Throw New InvalidOperationException("capture_display_replaced")
        End If
        Return match
    End Function

    Public Shared Function SaveSelection(root As String, requested As Choice,
                                         displays As IReadOnlyList(Of Display)) As Display
        If requested Is Nothing Then Throw New ArgumentException("capture_display_selection_missing")
        If displays Is Nothing Then Throw New ArgumentException("capture_display_unavailable")
        Dim exact = displays.FirstOrDefault(Function(d) d.OutputIndex = requested.OutputIndex AndAlso
                                             d.AdapterLuidLow = requested.AdapterLuidLow AndAlso
                                             d.AdapterLuidHigh = requested.AdapterLuidHigh)
        If exact Is Nothing Then Throw New ArgumentException("capture_display_not_found")
        Dim monitorPath = If(exact.Target?.MonitorDevicePath, "")
        Dim pathValue = SelectionPath(root)
        Directory.CreateDirectory(Path.GetDirectoryName(pathValue))
        Dim temp = pathValue & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temp, JsonSerializer.Serialize(New With {
                .schemaVersion = If(monitorPath.Length > 0, 2, 1),
                .outputIndex = requested.OutputIndex,
                .adapterLuidLow = requested.AdapterLuidLow,
                .adapterLuidHigh = requested.AdapterLuidHigh, .monitorDevicePath = monitorPath
            }))
            File.Move(temp, pathValue, True)
        Finally
            If File.Exists(temp) Then File.Delete(temp)
        End Try
        Return exact
    End Function
End Class
