// WasapiDirectInterop.cs — P13.1 spike: zero-dependency WASAPI COM interop.
//
//
// PROMOTED (P13.2) into CaptureEngine.Audio.Wasapi — byte-identical COM
// declarations; only the namespace changed. Runtime evidence: OWNER runs
// 2026-08-27 (60s audio + 60s silence), VERDICT PASS both, see
// spikes/V5_WASAPI_Position_Spike/evidence/ANALYSIS.md.
// NOTE: class stays internal — WasapiPositionCapture is the public surface.
// (visibility widened from internal to public inside this internal class
// is a no-op; the class itself is internal so no COM type leaks out.)
// WHY: NAudio's WasapiLoopbackCapture drops devicePosition/qpcPosition.
// This file declares just enough WASAPI to read them directly, so the spike
// can prove the DATA independent of any wrapper library. If the direct path
// works here, the P13.2 engine class can either keep this (~200 lines) or
// wrap it behind WasapiPositionCapture.
//
// v3 CHANGE — EVERY method is [PreserveSig] int, every HRESULT checked here.
// Rationale (learned the hard way on OWNER's machine): with plain 'void'
// declarations the CLR converts failing HRESULTs into exception TYPES via
// its fixed HRESULT map — E_OUTOFMEMORY (0x8007000E) becomes
// System.OutOfMemoryException("Out of memory.") even when the real failure
// is the audio engine refusing stream creation for a parameter reason.
// That mapping named the CLR table, not the failing call, and cost a whole
// debugging round-trip. Now: every failure prints "CallName: 0xHHHHHHHH
// (DECODED_NAME)".

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CaptureEngine.Audio.Wasapi
{
    internal static class WasapiDirectInterop
    {
        // ── Constants ────────────────────────────────────────────────────
        public const int CLSCTX_ALL = 0x17;                    // INPROC|LOCAL|REMOTE
        public const int eRender = 0;                          // DATA_FLOW
        public const int eCapture = 1;
        public const int eConsole = 0;                         // ROLE
        public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        public const int AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;

        public static readonly Guid CLSID_MMDeviceEnumerator =
            new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        public static readonly Guid IID_IAudioClient =
            new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
        public static readonly Guid IID_IAudioCaptureClient =
            new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317");

        // ── IMMDeviceEnumerator ──────────────────────────────────────────
        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices); // slot 0 (unused)
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);               // slot 3 (unused)
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);             // slot 4 (unused)
        }

        // ── IMMDevice ────────────────────────────────────────────────────
        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                          [MarshalAs(UnmanagedType.IUnknown)] out object iface);
            [PreserveSig] int OpenPropertyStore(IntPtr reserved);                                  // slot 1 (unused)
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        // ── IAudioClient ─────────────────────────────────────────────────
        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioClient
        {
            // pFormat: pass the RAW pointer from GetMixFormat — do NOT round-
            // trip through a WAVEFORMATEX struct copy. The modern mix format
            // is WAVE_FORMAT_EXTENSIBLE (tag=65534, cbSize=22 => 40 bytes);
            // a struct-copy buffer would be 18 bytes => E_INVALIDARG.
            // sessionGuid: IntPtr so we can pass NULL (documented as legal).
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration100ns,
                            long periodicity100ns, IntPtr pFormat, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint bufferFrames);
            [PreserveSig] int GetStreamLatency(out long latency100ns);
            [PreserveSig] int GetCurrentPadding(out uint paddingFrames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
            [PreserveSig] int GetMixFormat(out IntPtr ppFormat);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod100ns, out long minPeriod100ns);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        // ── IAudioCaptureClient ──────────────────────────────────────────
        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface IAudioCaptureClient
        {
            // THE payload of this spike: hardware stamps per packet.
            // devicePosition : device sample-clock frames since stream start
            // qpcPosition    : performance counter when the device read that
            //                  position (unit verified empirically by the
            //                  spike — QPC ticks vs 100ns, see README)
            [PreserveSig] int GetBuffer(out IntPtr data, out int framesInPacket, out int flags,
                           out long devicePosition, out long qpcPosition);
            // v4 FIX: capture-side ReleaseBuffer MUST receive the frame count
            // GetBuffer just reported. v3 passed 0 here (a render-side habit
            // that leaked into capture code) — the engine then NEVER advanced
            // the read cursor, GetNextPacketSize kept re-serving the same
            // packet, the loop spun at ~2.4M iters/s and piled up ~120M
            // duplicate rows until the CSV writer died with OutOfMemory.
            [PreserveSig] int ReleaseBuffer(int framesRead);
            [PreserveSig] int GetNextPacketSize(out int numFramesInNextPacket);
        }

        // ── WAVEFORMATEX ─────────────────────────────────────────────────
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        // ── Helpers ──────────────────────────────────────────────────────

        /// <summary>Default device (render for loopback, capture for mic).</summary>
        public static IMMDevice GetDevice(int dataFlow, string deviceId)
        {
            var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type);
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                Check(enumerator.GetDevice(deviceId, out IMMDevice selected),
                      "IMMDeviceEnumerator.GetDevice(" + deviceId + ")");
                return selected;
            }
            Check(enumerator.GetDefaultAudioEndpoint(dataFlow, eConsole, out IMMDevice device),
                  "IMMDeviceEnumerator.GetDefaultAudioEndpoint");
            return device;
        }

        public static IMMDevice GetDefaultDevice(int dataFlow) => GetDevice(dataFlow, "");

        /// <summary>Activate IAudioClient on a device.</summary>
        public static IAudioClient ActivateAudioClient(IMMDevice device)
        {
            var iid = IID_IAudioClient;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object raw),
                  "IMMDevice.Activate(IAudioClient)");
            return (IAudioClient)raw;
        }

        /// <summary>Get the capture service from an initialized client.</summary>
        public static IAudioCaptureClient GetCaptureClient(IAudioClient client)
        {
            var iid = IID_IAudioCaptureClient;
            Check(client.GetService(ref iid, out object raw),
                  "IAudioClient.GetService(IAudioCaptureClient)");
            return (IAudioCaptureClient)raw;
        }

        /// <summary>
        /// Throw an InvalidOperationException naming the exact call and the
        /// decoded HRESULT. Never rely on the CLR's HRESULT->exception map:
        /// it turned E_OUTOFMEMORY into a misleading "Out of memory." here.
        /// </summary>
        public static void Check(int hr, string call)
        {
            if (hr >= 0) return;
            throw new InvalidOperationException(
                call + " failed: " + HrName(hr));
        }

        /// <summary>Human-readable HRESULT suffix (empty when unknown).</summary>
        public static string HrName(int hr)
        {
            string name;
            return "0x" + hr.ToString("X8") + (Known.TryGetValue(hr, out name) ? " (" + name + ")" : "");
        }

        // Common WASAPI failures (mmdeviceapi.h / audioclient.h aud-clnt codes).
        private static readonly Dictionary<int, string> Known = new Dictionary<int, string>
        {
            { unchecked((int)0x88890001), "AUDCLNT_E_NOT_INITIALIZED" },
            { unchecked((int)0x88890002), "AUDCLNT_E_ALREADY_INITIALIZED" },
            { unchecked((int)0x88890003), "AUDCLNT_E_WRONG_ENDPOINT_TYPE" },
            { unchecked((int)0x88890004), "AUDCLNT_E_DEVICE_INVALIDATED" },
            { unchecked((int)0x88890005), "AUDCLNT_E_NOT_STOPPED" },
            { unchecked((int)0x88890006), "AUDCLNT_E_BUFFER_TOO_LARGE" },
            { unchecked((int)0x88890007), "AUDCLNT_E_OUT_OF_ORDER" },
            { unchecked((int)0x88890008), "AUDCLNT_E_UNSUPPORTED_FORMAT" },
            { unchecked((int)0x88890009), "AUDCLNT_E_INVALID_DEVICE_PERIOD" },
            { unchecked((int)0x8889000A), "AUDCLNT_E_INVALID_STREAM_FLAGS" },
            { unchecked((int)0x8889000B), "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED" },
            { unchecked((int)0x8889000D), "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED" },
            { unchecked((int)0x8889000E), "AUDCLNT_E_ENDPOINT_CREATE_FAILED" },
            { unchecked((int)0x8889000F), "AUDCLNT_E_SERVICE_NOT_RUNNING" },
            { unchecked((int)0x88890013), "AUDCLNT_E_EVENTHANDLE_NOT_SET" },
            { unchecked((int)0x88890014), "AUDCLNT_E_INCORRECT_BUFFER_SIZE" },
            { unchecked((int)0x88890015), "AUDCLNT_E_BUFFER_SIZE_ERROR" },
            { unchecked((int)0x88890016), "AUDCLNT_E_CPUUSAGE_EXCEEDED" },
            { unchecked((int)0x88890017), "AUDCLNT_E_BUFFER_ERROR" },
            { unchecked((int)0x88890018), "AUDCLNT_E_DEVICE_IN_USE" },
            { unchecked((int)0x8007000E), "E_OUTOFMEMORY" },
            { unchecked((int)0x80070057), "E_INVALIDARG" },
            { unchecked((int)0x80070005), "E_ACCESSDENIED" },
            { unchecked((int)0x80070006), "E_HANDLE" },
            { unchecked((int)0x80004005), "E_FAIL" },
            { unchecked((int)0x80004001), "E_NOTIMPL" },
        };

        public static string FormatToString(WAVEFORMATEX f)
        {
            return f.nChannels + "ch " + f.nSamplesPerSec + "Hz " +
                   f.wBitsPerSample + "bit tag=" + f.wFormatTag +
                   " blockAlign=" + f.nBlockAlign;
        }
    }
}
