Option Strict On
Imports System.Runtime.InteropServices
Imports System.Collections.Generic

Namespace DulukaEngine.Video.Backends.Ddagrab
    ' Read-only CCD metadata. No display configuration or capture API is called.
    Friend NotInheritable Class DisplayTopology
        <StructLayout(LayoutKind.Sequential)>
        Friend Structure Luid
            Public Low As UInteger
            Public High As Integer
        End Structure
        <StructLayout(LayoutKind.Sequential)>
        Private Structure Header
            Public Kind As UInteger
            Public Size As UInteger
            Public Adapter As Luid
            Public Id As UInteger
        End Structure
        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure AdapterName
            Public Header As Header
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=128)> Public Path As String
        End Structure
        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure SourceName
            Public Header As Header
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=32)> Public Name As String
        End Structure
        <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
        Private Structure TargetName
            Public Header As Header
            Public Flags, Technology As UInteger
            Public Manufacturer, Product As UShort
            Public Connector As UInteger
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=64)> Public FriendlyName As String
            <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=128)> Public MonitorPath As String
        End Structure
        Friend NotInheritable Class OutputInfo
            Public Property Technology As UInteger
            Public Property MonitorPath As String
        End Class
        <StructLayout(LayoutKind.Sequential)>
        Private Structure SourceInfo
            Public Adapter As Luid
            Public Id, ModeIndex, Status As UInteger
        End Structure
        <StructLayout(LayoutKind.Sequential)>
        Private Structure TargetInfo
            Public Adapter As Luid
            Public Id, ModeIndex, Technology, Rotation, Scaling, RefreshNum, RefreshDen, Scanline As UInteger
            Public Available As Integer
            Public Status As UInteger
        End Structure
        <StructLayout(LayoutKind.Sequential)>
        Private Structure PathInfo
            Public Source As SourceInfo
            Public Target As TargetInfo
            Public Flags As UInteger
        End Structure
        <StructLayout(LayoutKind.Sequential, Size:=64)>
        Private Structure ModeInfo
            Public Kind As UInteger
        End Structure
        <DllImport("user32.dll", EntryPoint:="DisplayConfigGetDeviceInfo")>
        Private Shared Function GetAdapter(ByRef name As AdapterName) As Integer
        End Function
        <DllImport("user32.dll", EntryPoint:="DisplayConfigGetDeviceInfo")>
        Private Shared Function GetSource(ByRef name As SourceName) As Integer
        End Function
        <DllImport("user32.dll", EntryPoint:="DisplayConfigGetDeviceInfo")>
        Private Shared Function GetTarget(ByRef name As TargetName) As Integer
        End Function
        <DllImport("user32.dll")>
        Private Shared Function GetDisplayConfigBufferSizes(flags As UInteger, ByRef paths As UInteger, ByRef modes As UInteger) As Integer
        End Function
        <DllImport("user32.dll")>
        Private Shared Function QueryDisplayConfig(flags As UInteger, ByRef pathCount As UInteger,
            <Out> paths As PathInfo(), ByRef modeCount As UInteger, <Out> modes As ModeInfo(), topology As IntPtr) As Integer
        End Function
        Friend Shared Function AdapterPath(low As UInteger, high As Integer) As String
            Dim name As New AdapterName With {.Header = New Header With {
                .Kind = 4UI, .Size = CUInt(Marshal.SizeOf(Of AdapterName)()), .Adapter = New Luid With {.Low = low, .High = high}}}
            If GetAdapter(name) <> 0 Then Return ""
            Return If(name.Path, "")
        End Function
        Friend Shared Function Key(low As UInteger, high As Integer, name As String) As String
            Return $"{high:x8}:{low:x8}:{name}"
        End Function
        Friend Shared Function ActiveTechnologies() As Dictionary(Of String, OutputInfo)
            Dim result As New Dictionary(Of String, OutputInfo)(StringComparer.OrdinalIgnoreCase)
            For attempt As Integer = 1 To 3
                Dim pc, mc As UInteger
                Dim rc = GetDisplayConfigBufferSizes(2UI, pc, mc)
                If rc <> 0 Then Throw New InvalidOperationException($"display_topology_sizes_failed:{rc}")
                If pc = 0 OrElse pc > 256 OrElse mc > 1024 Then Throw New InvalidOperationException("display_topology_invalid_size")
                Dim paths(CInt(pc) - 1) As PathInfo
                Dim modes(CInt(Math.Max(mc, 1UI)) - 1) As ModeInfo
                rc = QueryDisplayConfig(2UI, pc, paths, mc, modes, IntPtr.Zero)
                If rc = 122 Then Continue For ' topology changed between the two calls
                If rc <> 0 Then Throw New InvalidOperationException($"display_topology_query_failed:{rc}")
                For i As Integer = 0 To CInt(pc) - 1
                    Dim p = paths(i)
                    Dim sn As New SourceName With {.Header = New Header With {.Kind = 1UI,
                        .Size = CUInt(Marshal.SizeOf(Of SourceName)()), .Adapter = p.Source.Adapter, .Id = p.Source.Id}}
                    If GetSource(sn) <> 0 Then Throw New InvalidOperationException("display_topology_source_unavailable")
                    Dim tn As New TargetName With {.Header = New Header With {.Kind = 2UI,
                        .Size = CUInt(Marshal.SizeOf(Of TargetName)()), .Adapter = p.Target.Adapter, .Id = p.Target.Id}}
                    If GetTarget(tn) <> 0 Then Throw New InvalidOperationException("display_topology_target_unavailable")
                    Dim k = Key(p.Source.Adapter.Low, p.Source.Adapter.High, sn.Name)
                    ' Clone paths must not hide an indirect/virtual target behind a physical one.
                    If result.ContainsKey(k) AndAlso (result(k).Technology <> p.Target.Technology OrElse result(k).MonitorPath <> tn.MonitorPath) Then
                        result(k) = New OutputInfo With {.Technology = UInteger.MaxValue, .MonitorPath = ""}
                    Else
                        result(k) = New OutputInfo With {.Technology = p.Target.Technology, .MonitorPath = tn.MonitorPath}
                    End If
                Next
                Return result
            Next
            Throw New InvalidOperationException("display_topology_unstable")
        End Function
    End Class
End Namespace
