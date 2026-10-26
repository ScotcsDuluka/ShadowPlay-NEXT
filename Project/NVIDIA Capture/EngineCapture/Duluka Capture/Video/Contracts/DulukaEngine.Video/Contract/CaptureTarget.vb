Option Strict On
Imports System
Imports System.Collections.Generic
Imports System.Linq

Namespace DulukaEngine.Video
    ' PCI identifiers are catalog metadata, never an adapter-selection algorithm.
    Public Enum GpuVendor As UInteger
        Unknown = 0
        Intel = &H8086UI
        Nvidia = &H10DEUI
        Amd = &H1002UI
    End Enum

    Public NotInheritable Class AdapterIdentity
        Public ReadOnly Property Low As UInteger
        Public ReadOnly Property High As Integer
        Public ReadOnly Property VendorId As UInteger
        Public ReadOnly Property DeviceId As UInteger
        Public ReadOnly Property IsSoftware As Boolean
        Public ReadOnly Property DevicePath As String
        Public ReadOnly Property DeviceInstanceId As String
            Get
                If Not DevicePath.StartsWith("\\?\", StringComparison.OrdinalIgnoreCase) Then Return ""
                Dim parts = DevicePath.Substring(4).Split("#"c)
                If parts.Length < 4 Then Return ""
                Return String.Join("\", parts.Take(parts.Length - 1))
            End Get
        End Property
        Public ReadOnly Property Vendor As GpuVendor
            Get
                If [Enum].IsDefined(GetType(GpuVendor), VendorId) Then Return CType(VendorId, GpuVendor)
                Return GpuVendor.Unknown
            End Get
        End Property
        Public ReadOnly Property HasLuid As Boolean
            Get
                Return Low <> 0UI OrElse High <> 0
            End Get
        End Property
        Public ReadOnly Property IsPhysical As Boolean
            Get
                ' DisplayConfig's adapter path is tied to this LUID, not a WMI index.
                Return Not IsSoftware AndAlso DevicePath.StartsWith("\\?\PCI#", StringComparison.OrdinalIgnoreCase) AndAlso
                    DevicePath.Contains($"VEN_{VendorId:X4}&DEV_{DeviceId:X4}", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
        Public Sub New(low As UInteger, high As Integer, vendorId As UInteger, deviceId As UInteger,
                       isSoftware As Boolean, devicePath As String)
            Me.Low = low : Me.High = high : Me.VendorId = vendorId : Me.DeviceId = deviceId
            Me.IsSoftware = isSoftware : Me.DevicePath = If(devicePath, "")
        End Sub
        Public Function SameDevice(other As AdapterIdentity) As Boolean
            Return other IsNot Nothing AndAlso HasLuid AndAlso other.HasLuid AndAlso
                Low = other.Low AndAlso High = other.High AndAlso VendorId = other.VendorId AndAlso DeviceId = other.DeviceId
        End Function
        Public Overrides Function ToString() As String
            Return $"{High:x8}:{Low:x8} vendor={VendorId:x4} device={DeviceId:x4}"
        End Function
    End Class

    Public NotInheritable Class CaptureTarget
        Public ReadOnly Property Adapter As AdapterIdentity
        Public ReadOnly Property OutputIndex As Integer
        Public ReadOnly Property DeviceName As String
        Public ReadOnly Property Monitor As IntPtr
        Public ReadOnly Property Width As Integer
        Public ReadOnly Property Height As Integer
        Public ReadOnly Property Left As Integer
        Public ReadOnly Property Top As Integer
        Public ReadOnly Property Rotation As Integer
        Public ReadOnly Property Attached As Boolean
        Public ReadOnly Property OutputTechnology As UInteger?
        Public ReadOnly Property TopologyId As String
        Public ReadOnly Property MonitorDevicePath As String
        Public ReadOnly Property Eligible As Boolean
            Get
                Return Adapter IsNot Nothing AndAlso Adapter.HasLuid AndAlso Adapter.IsPhysical AndAlso
                    OutputIndex >= 0 AndAlso Attached AndAlso Width > 0 AndAlso Height > 0 AndAlso
                    Monitor <> IntPtr.Zero AndAlso OutputTechnology.HasValue AndAlso
                    Not String.IsNullOrWhiteSpace(MonitorDevicePath) AndAlso
                    OutputTechnology.Value <> 15UI AndAlso OutputTechnology.Value <> 16UI AndAlso
                    OutputTechnology.Value <> 17UI AndAlso OutputTechnology.Value <> UInteger.MaxValue
            End Get
        End Property
        Public Sub New(adapter As AdapterIdentity, outputIndex As Integer, deviceName As String,
                       monitor As IntPtr, width As Integer, height As Integer, left As Integer, top As Integer,
                       rotation As Integer, attached As Boolean, technology As UInteger?, topologyId As String,
                       Optional monitorDevicePath As String = "")
            Me.Adapter = adapter : Me.OutputIndex = outputIndex : Me.DeviceName = If(deviceName, "")
            Me.Monitor = monitor : Me.Width = width : Me.Height = height : Me.Left = left : Me.Top = top
            Me.Rotation = rotation : Me.Attached = attached : Me.OutputTechnology = technology
            Me.TopologyId = If(topologyId, "")
            Me.MonitorDevicePath = If(monitorDevicePath, "")
        End Sub
        Public Function SameOutput(other As CaptureTarget) As Boolean
            Return other IsNot Nothing AndAlso Adapter IsNot Nothing AndAlso Adapter.SameDevice(other.Adapter) AndAlso
                OutputIndex = other.OutputIndex AndAlso DeviceName = other.DeviceName AndAlso Monitor = other.Monitor AndAlso
                Width = other.Width AndAlso Height = other.Height AndAlso Left = other.Left AndAlso Top = other.Top AndAlso
                Rotation = other.Rotation AndAlso OutputTechnology.Equals(other.OutputTechnology) AndAlso
                String.Equals(MonitorDevicePath, other.MonitorDevicePath, StringComparison.OrdinalIgnoreCase)
        End Function
        Public Shared Function RequireCurrent(requested As CaptureTarget, current As IEnumerable(Of CaptureTarget)) As CaptureTarget
            If requested Is Nothing OrElse Not requested.Eligible Then Throw New InvalidOperationException("capture_target_ineligible")
            Dim matches = current.Where(Function(t) t.Eligible AndAlso requested.SameOutput(t)).ToArray()
            If matches.Length <> 1 Then Throw New InvalidOperationException("capture_target_stale_or_ambiguous")
            Return matches(0)
        End Function
    End Class
End Namespace
