// WgcFrameSource.cs — 60fps overlay frames via Windows.Graphics.Capture.
//
// Captures the WebView2 Chromium child window straight off the GPU
// (DWM surface — works while the game occludes the window; no PNG or
// base64 anywhere). Frames are un-premultiplied to straight alpha and
// handed to the host through FrameCallback; the host (HookCdpCapture.
// PublishPixels) writes them into the shared-memory frame the in-game
// DLL already renders. The CDP path stays as the fallback source.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace NvShareEngine
{
    public static class WgcFrameSource
    {
        // ── host wiring (set by the VB engine before Start) ──
        public static Action<int, int, IntPtr, int> FrameCallback;   // (w, h, pixels, rowPitch)
        public static Action<bool> ActiveChanged;                    // WGC took over / gave up
        public static Func<int> GateProbe;                           // returns HookCdpCapture.CaptureEnabled

        public static volatile bool Active;

        private static IntPtr _hostHwnd;
        private static Thread _worker;

        // ── logging ──
        private static readonly object LogLock = new object();
        private static void Log(string m)
        {
            try
            {
                var p = Path.Combine(AppContext.BaseDirectory, "Logs", "wgc.log");
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                lock (LogLock)
                    File.AppendAllText(p,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + m + Environment.NewLine);
            }
            catch { }
        }

        public static void Start(IntPtr webViewHostHwnd)
        {
            if (_worker != null) return;
            _hostHwnd = webViewHostHwnd;
            _worker = new Thread(Run) { IsBackground = true, Name = "WgcFrameSource" };
            _worker.Start();
        }

        private static void Run()
        {
            // the Chromium child appears shortly after navigation; retry
            for (int attempt = 0; attempt < 60; attempt++)
            {
                try
                {
                    if (!GraphicsCaptureSession.IsSupported())
                    {
                        Log("WGC not supported on this OS — CDP stays the source");
                        return;
                    }
                    if (_hostHwnd == IntPtr.Zero) { Thread.Sleep(1000); continue; }
                    // WGC rejects windows that were never shown (E_INVALIDARG,
                    // visible=False measured): the engine form shows on the
                    // first overlay toggle — wait for it, cheap
                    if (!IsWindowVisible(_hostHwnd)) { Thread.Sleep(1000); continue; }
                    // the TOP-LEVEL form window — CreateForWindow rejects
                    // child windows with E_INVALIDARG
                    Log("capturing top-level window " + _hostHwnd.ToString());
                    Capture(_hostHwnd);
                    return; // Capture only returns on fatal error
                }
                catch (Exception ex)
                {
                    Log("attempt " + attempt + " failed: " + ex.Message);
                    SetActive(false);
                    Thread.Sleep(2000);
                }
            }
            Log("gave up — CDP fallback owns capture");
            SetActive(false);
        }

        private static void SetActive(bool a)
        {
            Active = a;
            try { ActiveChanged?.Invoke(a); } catch { }
        }

        // ── per-session state ──
        private static GraphicsCaptureItem _item;
        private static IntPtr _device;
        private static IntPtr _deviceCom;     // ID3D11Device COM pointer (vtable calls)
        private static IntPtr _ctxCom;        // ID3D11DeviceContext COM pointer
        private static IntPtr _staging;       // ID3D11Texture2D COM pointer (readback)
        private static int _stageW, _stageH;
        private static Direct3D11CaptureFramePool _pool;
        private static GraphicsCaptureSession _session;
        private static long _frames;
        private static long _calls;

        private static void Capture(IntPtr hwnd)
        {
            // 1) our own D3D11 device (hardware, BGRA-capable)
            int fl = 0;
            IntPtr ctxOut = IntPtr.Zero;
            int hr = D3D11CreateDevice(IntPtr.Zero, 1 /*HARDWARE*/, IntPtr.Zero, 0x20 /*BGRA*/,
                IntPtr.Zero, 0, 7, out _device, out fl, out ctxOut);
            Log($"step1 hr=0x{hr:X8} fl=0x{fl:X} dev={_device} ctxOut={ctxOut}");
            if (hr < 0) throw new Exception("D3D11CreateDevice hr=0x" + hr.ToString("X8"));

            // immediate context straight from the creation out param
            _ctxCom = ctxOut;
            Log("ctx verified: " + _ctxCom.ToString());

            // 2) wrap it as a WinRT IDirect3DDevice
            Guid iidDxgi = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice
            hr = Marshal.QueryInterface(_device, ref iidDxgi, out IntPtr dxgiPtr);
            if (hr < 0) throw new Exception("QI IDXGIDevice hr=0x" + hr.ToString("X8"));
            try
            {
                hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out IntPtr inspectable);
                if (hr < 0) throw new Exception("CreateDirect3D11DeviceFromDXGIDevice hr=0x" + hr.ToString("X8"));
                IDirect3DDevice winrtDevice = null;
                try { winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }

                // 3) capture item for the window — CsWinRT's documented
                // interop pattern: the projected class exposes its
                // activation factory cast to the interop interface
                Log("step4 factory");
                var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
                var itemGuid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760"); // IGraphicsCaptureItem
                Log("step5 createForWindow");
                hr = interop.CreateForWindow(hwnd, ref itemGuid, out IntPtr itemPtr);
                if (hr < 0 || itemPtr == IntPtr.Zero)
                {
                    // diagnostics: what does the OS think of this window?
                    var sb = new System.Text.StringBuilder(128);
                    GetClassNameW(hwnd, sb, 128);
                    GetWindowRect(hwnd, out var r);
                    int winHr = hr;
                    Log("CreateForWindow hr=0x" + winHr.ToString("X8") +
                        " hwnd=" + hwnd + " class=[" + sb + "]" +
                        " rect=" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top) +
                        " visible=" + IsWindowVisible(hwnd));
                    if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
                    throw new Exception("CreateForWindow hr=0x" + winHr.ToString("X8"));
                }
                Log("step6 fromAbi");
                _item = GraphicsCaptureItem.FromAbi(itemPtr);
                Marshal.Release(itemPtr);
                var size = _item.Size;

                // 4) free-threaded pool: no DispatcherQueue needed
                Log("step7 pool");
                _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                _pool.FrameArrived += OnFrameArrived;
                _session = _pool.CreateCaptureSession(_item);
                try { _session.IsCursorCaptureEnabled = false; } catch { }
                try { _session.IsBorderRequired = false; } catch { }
                _session.StartCapture();

                SetActive(true);
                Log("capture running " + size.Width + "x" + size.Height);

                // FrameArrived does the work; keep this thread alive
                while (true) Thread.Sleep(1000);
            }
            finally
            {
                Marshal.Release(dxgiPtr);
            }
        }

        private static void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            try
            {
                using (var frame = sender.TryGetNextFrame())
                {
                    _calls++;
                    if (_calls <= 5) Log("handler call " + _calls + (frame == null ? " frame=NULL" : " size=" + frame.ContentSize.Width + "x" + frame.ContentSize.Height + " gate=" + (GateProbe != null ? GateProbe() : -1)));
                    if (frame == null) return;
                    int gate = GateProbe != null ? GateProbe() : 1;
                    if (gate == 0) return;   // overlay closed — nothing to publish
                    int w = frame.ContentSize.Width;
                    int h = frame.ContentSize.Height;
                    if (w <= 0 || h <= 0) return;
                    if (FrameCallback == null) return;
                    Log("vstep enter");

                    // surface → D3D11 texture pointer (our device — the wrap
                    // above guarantees the surface lives on it)
                    IntPtr surfacePtr = ((IWinRTObject)frame.Surface).NativeObject.ThisPtr;
                    var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfacePtr);
                    Guid iidTex = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    IntPtr frameTex = access.GetInterface(ref iidTex);
                    if (frameTex == IntPtr.Zero) return;
                    try
                    {
                        // (re)create the staging readback texture on size change
                        if (_staging == IntPtr.Zero || _stageW != w || _stageH != h)
                        {
                            if (_staging != IntPtr.Zero) Marshal.Release(_staging);
                            _staging = IntPtr.Zero;
                            var d = new TEX2D_DESC
                            {
                                Width = (uint)w,
                                Height = (uint)h,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = 87,             // B8G8R8A8_UNORM
                                SampleCount = 1,
                                Usage = 3,               // STAGING
                                CPUAccessFlags = 0x20000 // READ
                            };
                            var devCom = (ID3D11DeviceCom)Marshal.GetObjectForIUnknown(_device);
                            int hr2 = devCom.CreateTexture2D(ref d, IntPtr.Zero, out _staging);
                            if (hr2 < 0) { Log("CreateTexture2D hr=0x" + hr2.ToString("X8")); return; }
_stageW = w;
                            _stageH = h;
                        }

                        var ctx = (ID3D11DeviceContextCom)Marshal.GetObjectForIUnknown(_ctxCom);
                        ctx.CopyResource(_staging, frameTex);
                        int mhr = ctx.Map(_staging, 0, 1 /*READ*/, 0, out var mapped);
                        if (mhr >= 0 && mapped.PData != IntPtr.Zero)
                        {
                            // premultiplied → straight alpha (the DLL blends with
                            // SRC_ALPHA/INV_SRC_ALPHA — correct for straight data;
                            // the PNG path is straight too, so one blend state
                            // serves both sources)
                            UnpremultiplyAndPublish(w, h, mapped.PData, (int)mapped.RowPitch);
                            ctx.Unmap(_staging, 0);
                            _frames++;
                            if (_frames == 1 || _frames % 300 == 0)
                                Log("frame #" + _frames + " " + w + "x" + h);
                        }
                        else if (mhr < 0)
                        {
                            Log("Map hr=0x" + mhr.ToString("X8"));
                        }
                    }
                    finally
                    {
                        Marshal.Release(frameTex);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("frame error: " + ex.Message);
            }
        }

        // ── premultiplied → straight + publish ──
        private static byte[] _buf = new byte[0];
        private static GCHandle _pin;

        private static unsafe void UnpremultiplyAndPublish(int w, int h, IntPtr src, int rowPitch)
        {
            int total = rowPitch * h;
            if (_buf.Length < total) _buf = new byte[total];
            Marshal.Copy(src, _buf, 0, total);

            fixed (byte* p = _buf)
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = p + y * rowPitch;
                    for (int x = 0; x < w; x++)
                    {
                        byte* px = row + x * 4;
                        uint a = px[3];
                        if (a == 255) continue;
                        if (a == 0) { px[0] = px[1] = px[2] = 0; continue; }
                        px[0] = (byte)(px[0] * 255u / a);
                        px[1] = (byte)(px[1] * 255u / a);
                        px[2] = (byte)(px[2] * 255u / a);
                    }
                }
            }

            _pin = GCHandle.Alloc(_buf, GCHandleType.Pinned);
            try
            {
                FrameCallback?.Invoke(w, h, _pin.AddrOfPinnedObject(), rowPitch);
            }
            finally
            {
                _pin.Free();
            }
        }

        // ── Win32 helpers ──
        private static IntPtr FindChromeChild(IntPtr root)
        {
            if (root == IntPtr.Zero) return IntPtr.Zero;
            IntPtr f = FindWindowEx(root, IntPtr.Zero, "Chrome_WidgetWin_0", null);
            if (f == IntPtr.Zero) f = FindWindowEx(root, IntPtr.Zero, null, null);
            return f;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hwnd, [Out] System.Text.StringBuilder sb, int max);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr l);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        // ── D3D11 interop (minimal vtables) ──
        [StructLayout(LayoutKind.Sequential)]
        private struct TEX2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality, Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MAPPED_SUBRESOURCE
        {
            public IntPtr PData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11DeviceCom
        {
            [PreserveSig] int Slot0(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot1(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int CreateTexture2D(ref TEX2D_DESC desc, IntPtr initial, out IntPtr texture);
            [PreserveSig] int Slot3(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot4(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot5(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot6(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot7(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot8(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot9(IntPtr a, IntPtr b, IntPtr c);
            [PreserveSig] int Slot10(IntPtr a);
            [PreserveSig] int Slot11(IntPtr a);
            [PreserveSig] int Slot12(IntPtr a);
            [PreserveSig] int Slot13(IntPtr a);
            [PreserveSig] int Slot14(IntPtr a);
            [PreserveSig] int Slot15(IntPtr a);
            [PreserveSig] int Slot16(IntPtr a);
            [PreserveSig] int Slot17(IntPtr a);
            [PreserveSig] int Slot18(IntPtr a);
            [PreserveSig] int Slot19(IntPtr a);
            [PreserveSig] int Slot20(IntPtr a);
            [PreserveSig] int Slot21(IntPtr a);
            [PreserveSig] int Slot22(IntPtr a);
            [PreserveSig] int Slot23(IntPtr a);
            [PreserveSig] int Slot24(IntPtr a);
            [PreserveSig] int Slot25(IntPtr a);
            [PreserveSig] int Slot26(IntPtr a);
            [PreserveSig] int Slot27(IntPtr a);
            [PreserveSig] int Slot28(IntPtr a);
            [PreserveSig] int Slot29(IntPtr a);
            [PreserveSig] int Slot30(IntPtr a);
            [PreserveSig] int Slot31(IntPtr a);
            [PreserveSig] int Slot32(IntPtr a);
            [PreserveSig] int Slot33(IntPtr a);
            [PreserveSig] int Slot34(IntPtr a);
            [PreserveSig] int Slot35(IntPtr a);
            [PreserveSig] int Slot36(IntPtr a);
            [PreserveSig] int Slot37(IntPtr a);
            void GetImmediateContext(out IntPtr context);   // slot 38
        }

        [ComImport, Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11DeviceContextCom
        {
            // ID3D11DeviceChild (4)
            [PreserveSig] int DevChild0(IntPtr a);
            [PreserveSig] int DevChild1(IntPtr a);
            [PreserveSig] int DevChild2(IntPtr a);
            [PreserveSig] int DevChild3(IntPtr a);
            [PreserveSig] int M0_VSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 7
            [PreserveSig] int M1_PSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 8
            [PreserveSig] int M2_PSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 9
            [PreserveSig] int M3_PSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 10
            [PreserveSig] int M4_VSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 11
            [PreserveSig] int M5_DrawIndexed(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 12
            [PreserveSig] int M6_Draw(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 13
            [PreserveSig] int Map(IntPtr resource, uint subresource, uint mapType, uint flags, out MAPPED_SUBRESOURCE mapped);   // slot 14
            void Unmap(IntPtr resource, uint subresource);   // slot 15
            [PreserveSig] int M9_PSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 16
            [PreserveSig] int M10_IASetInputLayout(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 17
            [PreserveSig] int M11_IASetVertexBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 18
            [PreserveSig] int M12_IASetIndexBuffer(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 19
            [PreserveSig] int M13_DrawIndexedInstanced(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 20
            [PreserveSig] int M14_DrawInstanced(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 21
            [PreserveSig] int M15_GSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 22
            [PreserveSig] int M16_GSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 23
            [PreserveSig] int M17_IASetPrimitiveTopology(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 24
            [PreserveSig] int M18_VSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 25
            [PreserveSig] int M19_VSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 26
            [PreserveSig] int M20_Begin(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 27
            [PreserveSig] int M21_End(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 28
            [PreserveSig] int M22_GetData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 29
            [PreserveSig] int M23_SetPredication(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 30
            [PreserveSig] int M24_GSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 31
            [PreserveSig] int M25_GSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 32
            [PreserveSig] int M26_OMSetRenderTargets(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 33
            [PreserveSig] int M27_OMSetRenderTargetsAndUnorderedAccessViews(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 34
            [PreserveSig] int M28_OMSetBlendState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 35
            [PreserveSig] int M29_OMSetDepthStencilState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 36
            [PreserveSig] int M30_SOSetTargets(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 37
            [PreserveSig] int M31_DrawAuto(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 38
            [PreserveSig] int M32_DrawIndexedInstancedIndirect(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 39
            [PreserveSig] int M33_DrawInstancedIndirect(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 40
            [PreserveSig] int M34_Dispatch(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 41
            [PreserveSig] int M35_DispatchIndirect(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 42
            [PreserveSig] int M36_RSSetState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 43
            [PreserveSig] int M37_RSSetViewports(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 44
            [PreserveSig] int M38_RSSetScissorRects(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 45
            [PreserveSig] int M39_CopySubresourceRegion(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 46
            void CopyResource(IntPtr dst, IntPtr src);   // slot 47
            [PreserveSig] int M41_UpdateSubresource(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 48
            [PreserveSig] int M42_CopyStructureCount(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 49
            [PreserveSig] int M43_ClearRenderTargetView(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 50
            [PreserveSig] int M44_ClearUnorderedAccessViewUint(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 51
            [PreserveSig] int M45_ClearUnorderedAccessViewFloat(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 52
            [PreserveSig] int M46_ClearDepthStencilView(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 53
            [PreserveSig] int M47_GenerateMips(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 54
            [PreserveSig] int M48_SetResourceMinLOD(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 55
            [PreserveSig] int M49_ResolveSubresource(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 56
            [PreserveSig] int M50_ExecuteCommandList(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 57
            [PreserveSig] int M51_HSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 58
            [PreserveSig] int M52_HSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 59
            [PreserveSig] int M53_HSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 60
            [PreserveSig] int M54_HSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 61
            [PreserveSig] int M55_DSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 62
            [PreserveSig] int M56_DSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 63
            [PreserveSig] int M57_DSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 64
            [PreserveSig] int M58_DSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 65
            [PreserveSig] int M59_CSSetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 66
            [PreserveSig] int M60_CSSetUnorderedAccessViews(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 67
            [PreserveSig] int M61_CSSetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 68
            [PreserveSig] int M62_CSSetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 69
            [PreserveSig] int M63_CSSetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 70
            [PreserveSig] int M64_VSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 71
            [PreserveSig] int M65_PSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 72
            [PreserveSig] int M66_PSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 73
            [PreserveSig] int M67_PSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 74
            [PreserveSig] int M68_VSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 75
            [PreserveSig] int M69_PSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 76
            [PreserveSig] int M70_IAGetInputLayout(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 77
            [PreserveSig] int M71_IAGetVertexBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 78
            [PreserveSig] int M72_IAGetIndexBuffer(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 79
            [PreserveSig] int M73_GSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 80
            [PreserveSig] int M74_GSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 81
            [PreserveSig] int M75_IAGetPrimitiveTopology(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 82
            [PreserveSig] int M76_VSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 83
            [PreserveSig] int M77_VSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 84
            [PreserveSig] int M78_GetPredication(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 85
            [PreserveSig] int M79_GSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 86
            [PreserveSig] int M80_GSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 87
            [PreserveSig] int M81_OMGetRenderTargets(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 88
            [PreserveSig] int M82_OMGetRenderTargetsAndUnorderedAccessViews(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 89
            [PreserveSig] int M83_OMGetBlendState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 90
            [PreserveSig] int M84_OMGetDepthStencilState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 91
            [PreserveSig] int M85_SOGetTargets(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 92
            [PreserveSig] int M86_RSGetState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 93
            [PreserveSig] int M87_RSGetViewports(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 94
            [PreserveSig] int M88_RSGetScissorRects(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 95
            [PreserveSig] int M89_HSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 96
            [PreserveSig] int M90_HSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 97
            [PreserveSig] int M91_HSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 98
            [PreserveSig] int M92_HSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 99
            [PreserveSig] int M93_DSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 100
            [PreserveSig] int M94_DSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 101
            [PreserveSig] int M95_DSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 102
            [PreserveSig] int M96_DSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 103
            [PreserveSig] int M97_CSGetShaderResources(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 104
            [PreserveSig] int M98_CSGetUnorderedAccessViews(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 105
            [PreserveSig] int M99_CSGetShader(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 106
            [PreserveSig] int M100_CSGetSamplers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 107
            [PreserveSig] int M101_CSGetConstantBuffers(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 108
            [PreserveSig] int M102_ClearState(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 109
            [PreserveSig] int M103_Flush(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 110
            [PreserveSig] int M104_GetContextFlags(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 111
            [PreserveSig] int M105_FinishCommandList(IntPtr a, IntPtr b, IntPtr c, IntPtr d);   // slot 112
        }

        [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            [PreserveSig] int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);
            [PreserveSig] int CreateForMonitor(IntPtr hmon, ref Guid iid, out IntPtr result);
        }

        [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface(ref Guid iid);
        }

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr adapter, uint driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr winrtDevice);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(IntPtr activationId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll")]
        private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);
    }
}
