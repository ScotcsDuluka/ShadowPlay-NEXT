Option Strict On
Imports System.Collections.Generic
Imports Vortice.DXGI

Namespace DulukaEngine.Video.Backends.Ddagrab
    Public NotInheritable Class DxgiDisplayInventory
        Public NotInheritable Class AdapterEntry
            Public ReadOnly Property Identity As AdapterIdentity
            Public ReadOnly Property Index As Integer
            Public ReadOnly Property Description As String
            Public Sub New(identity As AdapterIdentity, index As Integer, description As String)
                Me.Identity = identity : Me.Index = index : Me.Description = description
            End Sub
        End Class
        Public NotInheritable Class Snapshot
            Public ReadOnly Property Adapters As IReadOnlyList(Of AdapterEntry)
            Public ReadOnly Property Outputs As IReadOnlyList(Of CaptureTarget)
            Public Sub New(adapters As List(Of AdapterEntry), outputs As List(Of CaptureTarget))
                Me.Adapters = adapters.AsReadOnly() : Me.Outputs = outputs.AsReadOnly()
            End Sub
        End Class
        Public Shared Function ResolveAdapterIndex(target As CaptureTarget, snapshot As Snapshot) As Integer
            CaptureTarget.RequireCurrent(target, snapshot.Outputs)
            Dim matches = snapshot.Adapters.Where(Function(a) a.Identity.SameDevice(target.Adapter)).ToArray()
            If matches.Length <> 1 OrElse matches(0).Index < 0 Then Throw New InvalidOperationException("capture_adapter_stale_or_ambiguous")
            Return matches(0).Index
        End Function

        ' CLI selection evidence only; it does not attest the child's actual device LUID.
        Public Shared Function CaptureDeviceArguments(target As CaptureTarget, snapshot As Snapshot) As String()
            Dim index = ResolveAdapterIndex(target, snapshot)
            Return {"-init_hw_device", $"d3d11va=capture:{index}", "-filter_hw_device", "capture"}
        End Function
        Public Shared Function Collect() As Snapshot
            Dim topology = DisplayTopology.ActiveTechnologies()
            Dim generation = Guid.NewGuid().ToString("N")
            Dim adapters As New List(Of AdapterEntry)
            Dim outputs As New List(Of CaptureTarget)
            Using factory = DXGI.CreateDXGIFactory1(Of IDXGIFactory1)()
                Dim ai As UInteger = 0UI
                Do
                    Dim adapter As IDXGIAdapter1 = Nothing
                    Dim rc = factory.EnumAdapters1(ai, adapter)
                    If rc = ResultCode.NotFound Then Exit Do
                    rc.CheckError()
                    Using adapter
                        Dim d = adapter.Description1
                        Dim identity As New AdapterIdentity(d.Luid.LowPart, d.Luid.HighPart, d.VendorId, d.DeviceId,
                            (d.Flags And AdapterFlags.Software) <> 0,
                            DisplayTopology.AdapterPath(d.Luid.LowPart, d.Luid.HighPart))
                        adapters.Add(New AdapterEntry(identity, CInt(ai), d.Description))
                        Dim oi As UInteger = 0UI
                        Do
                            Dim output As IDXGIOutput = Nothing
                            rc = adapter.EnumOutputs(oi, output)
                            If rc = ResultCode.NotFound Then Exit Do
                            rc.CheckError()
                            Using output
                                Dim od = output.Description
                                Dim b = od.DesktopCoordinates
                                Dim info As DisplayTopology.OutputInfo = Nothing
                                Dim known = topology.TryGetValue(DisplayTopology.Key(identity.Low, identity.High, od.DeviceName), info)
                                outputs.Add(New CaptureTarget(identity, CInt(oi), od.DeviceName, od.Monitor,
                                    b.Right - b.Left, b.Bottom - b.Top, b.Left, b.Top, CInt(od.Rotation),
                                    od.AttachedToDesktop, If(known, CType(info.Technology, UInteger?), Nothing), generation,
                                    If(known, info.MonitorPath, "")))
                            End Using
                            oi += 1UI
                        Loop
                    End Using
                    ai += 1UI
                Loop
            End Using
            Return New Snapshot(adapters, outputs)
        End Function
    End Class
End Namespace
