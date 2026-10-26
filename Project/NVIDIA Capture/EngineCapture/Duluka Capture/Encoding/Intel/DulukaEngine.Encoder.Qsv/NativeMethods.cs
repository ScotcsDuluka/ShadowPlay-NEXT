using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32)]

namespace DulukaEngine.Encoder.Qsv;

internal sealed class QsvHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal QsvHandle(IntPtr value) : base(true) { SetHandle(value); }
    protected override bool ReleaseHandle() { NativeMethods.Destroy(handle); return true; }
}

internal static class NativeMethods
{
    const string Library = "DulukaQsvNative.dll";
    [DllImport(Library, EntryPoint="dc_qsv_abi_version", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int AbiVersion();
    [DllImport(Library, EntryPoint="dc_qsv_create", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Create(uint low, int high, int width, int height, int fps, int gop, int rc,
        uint bitrate, int qp, int depth, int timeout, int color, out IntPtr handle, StringBuilder error, int errorSize);
    [DllImport(Library, EntryPoint="dc_qsv_identity", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Identity(QsvHandle handle, out uint low, out int high, out uint vendor,
        out uint device, out uint major, out uint minor, out uint impl);
    [DllImport(Library, EntryPoint="dc_qsv_begin", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Begin(QsvHandle handle);
    [DllImport(Library, EntryPoint="dc_qsv_submit", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Submit(QsvHandle handle, byte[] data, int length, int stride, ulong pts);
    [DllImport(Library, EntryPoint="dc_qsv_drain", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Drain(QsvHandle handle);
    [DllImport(Library, EntryPoint="dc_qsv_receive", CallingConvention=CallingConvention.Cdecl)]
    internal static extern int Receive(QsvHandle handle, byte[] output, int capacity, out int length,
        out ulong pts, out long dts, out int key, int waitMs);
    [DllImport(Library, EntryPoint="dc_qsv_counter", CallingConvention=CallingConvention.Cdecl)]
    internal static extern ulong Counter(QsvHandle handle, int field);
    [DllImport(Library, EntryPoint="dc_qsv_error", CallingConvention=CallingConvention.Cdecl)]
    internal static extern IntPtr Error(QsvHandle handle);
    [DllImport(Library, EntryPoint="dc_qsv_destroy", CallingConvention=CallingConvention.Cdecl)]
    internal static extern void Destroy(IntPtr handle);
}
