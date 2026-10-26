Option Strict On
Option Explicit On
Option Infer On
Imports System
Imports System.IO
Imports System.Collections.Generic
Imports DulukaEngine.Video
Imports DulukaEngine.Video.Backends.Ddagrab

Module Program
    Private count As Integer
    Sub Test(name As String, ok As Boolean)
        If Not ok Then Throw New Exception("FAIL " & name)
        count += 1
        Console.WriteLine("PASS " & name)
    End Sub
    Function Failed(work As Action) As Boolean
        Try
            work()
            Return False
        Catch ex As ArgumentException
            Return True
        Catch ex As InvalidOperationException
            Return True
        Catch ex As InvalidDataException
            Return True
        End Try
    End Function
    Sub Main()
        Dim root = Path.Combine(Path.GetTempPath(), "capture-display-test-" & Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root)
        Dim first As New CaptureDisplayCatalog.Display With {.OutputIndex = 0, .AdapterLuidLow = 13UI, .AdapterLuidHigh = 9, .Primary = True, .Width = 1680, .Height = 1050}
        Dim portrait As New CaptureDisplayCatalog.Display With {.OutputIndex = 2, .AdapterLuidLow = 13UI, .AdapterLuidHigh = 9, .Primary = False, .Width = 1080, .Height = 1920}
        Dim displays As New List(Of CaptureDisplayCatalog.Display) From {first, portrait}
        Try
            Test("no saved monitor defaults to first connected", CaptureDisplayCatalog.Resolve(root, displays).OutputIndex = 0)
            Dim desired As New CaptureDisplayCatalog.Choice With {.OutputIndex = 2, .AdapterLuidLow = 13UI, .AdapterLuidHigh = 9}
            CaptureDisplayCatalog.SaveSelection(root, desired, displays)
            Test("can select portrait monitor", CaptureDisplayCatalog.Resolve(root, displays).OutputIndex = 2)
            Test("saved output includes adapter identity", CaptureDisplayCatalog.LoadSelection(root).AdapterLuidHigh = 9)
            Test("disconnect fails closed", Failed(Sub() CaptureDisplayCatalog.Resolve(root, New List(Of CaptureDisplayCatalog.Display) From {first})))
            Test("different GPU refused", Failed(Sub() CaptureDisplayCatalog.SaveSelection(root, New CaptureDisplayCatalog.Choice With {.OutputIndex = 2, .AdapterLuidLow = 17UI, .AdapterLuidHigh = 9}, displays)))
            Test("saved choice not overwritten by rejected request", CaptureDisplayCatalog.Resolve(root, displays).OutputIndex = 2)
            Test("null selection refused", Failed(Sub() CaptureDisplayCatalog.SaveSelection(root, Nothing, displays)))
            Dim args = CaptureScreenshotEngine.BuildArguments(2, Path.Combine(root, "screen.png"))
            Test("single-frame screenshot uses exact DXGI output", Array.IndexOf(args, "ddagrab=output_idx=2:draw_mouse=1:framerate=1:dup_frames=0") >= 0)
            Test("hardware texture downloaded for PNG", Array.IndexOf(args, "hwdownload,format=bgra") >= 0)
            Test("no screenshot NVENC or audio", Array.IndexOf(args, "h264_nvenc") < 0 AndAlso Array.IndexOf(args, "-an") >= 0)
            Test("bad output number refused", Failed(Sub() CaptureScreenshotEngine.BuildArguments(-1, Path.Combine(root, "screen.png"))))
            Test("non-PNG destination refused", Failed(Sub() CaptureScreenshotEngine.BuildArguments(0, Path.Combine(root, "test.mp4"))))
            Test("bad/truncated PNG refused", Not CaptureScreenshotEngine.HasCompletePngSignature(New Byte() {1, 2, 3}))
            Dim header(44) As Byte
            Dim pngSig = New Byte() {137, 80, 78, 71, 13, 10, 26, 10}
            Array.Copy(pngSig, header, 8)
            header(12) = 73 : header(13) = 72 : header(14) = 68 : header(15) = 82
            header(19) = 1 : header(23) = 1
            Test("missing IEND is refused", Not CaptureScreenshotEngine.HasCompletePngSignature(header))
            Dim iend = New Byte() {0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130}
            Array.Copy(iend, 0, header, header.Length - iend.Length, iend.Length)
            Test("synthetic PNG header with IEND accepted", CaptureScreenshotEngine.HasCompletePngSignature(header))
            File.WriteAllText(CaptureDisplayCatalog.SelectionPath(root), "{}")
            Test("corrupt saved JSON refuses to change monitor", Failed(Sub() CaptureDisplayCatalog.Resolve(root, displays)))
            Dim identity As New AdapterIdentity(13UI, 9, CUInt(GpuVendor.Intel), &H1234UI, False, "\\?\PCI#VEN_8086&DEV_1234#test")
            portrait.Target = New CaptureTarget(identity, 2, "TEST", New IntPtr(1), 1080, 1920, 0, 0, 1, True, 5UI, "test", "monitor-original")
            CaptureDisplayCatalog.SaveSelection(root, desired, displays)
            Test("v2 persists CCD monitor identity", CaptureDisplayCatalog.LoadSelection(root).MonitorDevicePath = "monitor-original")
            Test("v2 preserves selected monitor", CaptureDisplayCatalog.Resolve(root, displays) Is portrait)
            Dim serialized = System.Text.Json.JsonSerializer.Serialize(portrait)
            Test("REST display JSON excludes process-local handles", Not serialized.Contains("Target") AndAlso Not serialized.Contains("Monitor"))
            portrait.Target = New CaptureTarget(identity, 2, "TEST", New IntPtr(2), 1080, 1920, 0, 0, 1, True, 5UI, "test", "monitor-replacement")
            Test("persisted monitor swap refused at same LUID and output index", Failed(Sub() CaptureDisplayCatalog.Resolve(root, displays)))
            File.WriteAllText(CaptureDisplayCatalog.SelectionPath(root), "{""schemaVersion"":2,""outputIndex"":2,""adapterLuidLow"":13,""adapterLuidHigh"":9}")
            Test("v2 cannot drop required monitor identity", Failed(Sub() CaptureDisplayCatalog.Resolve(root, displays)))
            Console.WriteLine("DISPLAY_SCREENSHOT_OFFLINE_PASS checks=" & count & " real_screen_read=false")
        Finally
            Directory.Delete(root, True)
        End Try
    End Sub
End Module
