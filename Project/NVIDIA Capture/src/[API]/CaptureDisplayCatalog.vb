Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.Json
Imports Vortice.DXGI

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
    End Class

    Public NotInheritable Class Choice
        Public Property OutputIndex As Integer
        Public Property AdapterLuidLow As UInteger
        Public Property AdapterLuidHigh As Integer
    End Class

    Public Shared Function Enumerate() As List(Of Display)
        Dim found As New List(Of Display)()
        Using factory As IDXGIFactory1 = DXGI.CreateDXGIFactory1(Of IDXGIFactory1)()
            Dim adapterIndex As UInteger = 0UI
            Dim selected As IDXGIAdapter1 = Nothing
            While adapterIndex < 32UI
                Dim adapter As IDXGIAdapter1 = Nothing
                If Not factory.EnumAdapters1(adapterIndex, adapter).Success Then Exit While
                If adapter.Description1.VendorId = &H10DEUI Then
                    selected = adapter
                    Exit While
                End If
                adapter.Dispose()
                adapterIndex += 1UI
            End While
            If selected Is Nothing Then Return found
            Using selected
                Dim identity = selected.Description1.Luid
                Dim index As UInteger = 0UI
                While index < 32UI
                    Dim output As IDXGIOutput = Nothing
                    If Not selected.EnumOutputs(index, output).Success Then Exit While
                    Using output
                        Dim description = output.Description
                        If description.AttachedToDesktop Then
                            Dim bounds = description.DesktopCoordinates
                            found.Add(New Display With {
                                .OutputIndex = CInt(index),
                                .AdapterLuidLow = identity.LowPart,
                                .AdapterLuidHigh = identity.HighPart,
                                .DeviceName = description.DeviceName,
                                .Width = bounds.Right - bounds.Left,
                                .Height = bounds.Bottom - bounds.Top,
                                .Left = bounds.Left,
                                .Top = bounds.Top,
                                .Primary = (bounds.Left = 0 AndAlso bounds.Top = 0),
                                .Label = $"Display {found.Count + 1} - {bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top}" &
                                         If(bounds.Left = 0 AndAlso bounds.Top = 0, " (Primary)", "")
                            })
                        End If
                    End Using
                    index += 1UI
                End While
            End Using
        End Using
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
               version.ValueKind <> JsonValueKind.Number OrElse version.GetInt32() <> 1 OrElse
               Not json.TryGetProperty("outputIndex", outputIndex) OrElse
               Not json.TryGetProperty("adapterLuidLow", low) OrElse
               Not json.TryGetProperty("adapterLuidHigh", high) Then
                Throw New InvalidDataException("invalid_display_selection")
            End If
            Return New Choice With {
                .OutputIndex = outputIndex.GetInt32(),
                .AdapterLuidLow = low.GetUInt32(),
                .AdapterLuidHigh = high.GetInt32()
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
        Dim pathValue = SelectionPath(root)
        Directory.CreateDirectory(Path.GetDirectoryName(pathValue))
        Dim temp = pathValue & "." & Guid.NewGuid().ToString("N") & ".tmp"
        Try
            File.WriteAllText(temp, JsonSerializer.Serialize(New With {
                .schemaVersion = 1,
                .outputIndex = requested.OutputIndex,
                .adapterLuidLow = requested.AdapterLuidLow,
                .adapterLuidHigh = requested.AdapterLuidHigh
            }))
            File.Move(temp, pathValue, True)
        Finally
            If File.Exists(temp) Then File.Delete(temp)
        End Try
        Return exact
    End Function
End Class
