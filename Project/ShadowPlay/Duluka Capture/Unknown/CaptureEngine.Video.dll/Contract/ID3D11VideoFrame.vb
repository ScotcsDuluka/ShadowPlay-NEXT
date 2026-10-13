Option Strict On
Option Explicit On
Option Infer On

' ID3D11VideoFrame.vb
'
' Extension of IVideoFrame that exposes the underlying D3D11 texture pointer
' for zero-copy GPU encoding.
'
' LOCATION RATIONALE (Phase 12 dependency audit):
'   This interface lives in CaptureEngine.Video (contract layer), NOT in
'   CaptureEngine.Encoder.Nvenc (concrete encoder project).
'
'   Why: D3D11-producing backends (CaptureEngine.Video.Ddagrab) must
'   implement this interface on their frame classes. If the interface
'   lived in CaptureEngine.Encoder.Nvenc, then Ddagrab would need a
'   ProjectReference to Encoder.Nvenc — creating a backward dependency
'   (Video backend → Encoder implementation) that violates the
'   replaceability principle. Encoder implementations should be swappable
'   without affecting capture backends.
'
'   By placing the interface in CaptureEngine.Video:
'     - Ddagrab can implement ID3D11VideoFrame (depends on Video contract only)
'     - NvencEncoderBackend can consume ID3D11VideoFrame (depends on Video
'       contract only — same dependency it already has for IVideoFrame)
'     - No backward dependency from Video → Encoder
'     - No circular reference
'
' FOUNDATION COMPLIANCE:
'   This is an ADDITIVE interface — it does NOT modify IVideoFrame or any
'   existing Foundation contract. CaptureEngine.Video is the contract layer
'   (not Foundation — Foundation is CaptureEngine/Engine/CaptureEngine.vb).
'   Adding new contract files to CaptureEngine.Video is permitted.
'
' BACKGROUND:
'   Foundation IVideoFrame does NOT expose native resource handles —
'   it carries Origin, PixelFormat, Dimensions, and Diagnostics only.
'   This is intentional: the contract supports CPU-memory frames as
'   well as GPU textures.
'
'   NvencEncoderBackend needs the D3D11 texture pointer to call
'   CopyResource (frame texture → encoder texture). Without an extension
'   interface, the encoder would need reflection or a TryCast hack —
'   both rejected per OWNER requirement.
'
' CONTRACT:
'   - Inherits CaptureEngine.Video.IVideoFrame (so existing contract is unchanged)
'   - Adds NativeTexture As IntPtr (the ID3D11Texture2D native pointer)
'   - Frame is still BORROWED — encoder MUST NOT dispose the texture
'     (the frame owner — typically DdagrabBackend — disposes it)
'
' HARD RULES COMPLIANCE:
'   ✅ Foundation contract unchanged (CaptureEngine.Video.IVideoFrame is in
'      CaptureEngine.Video, not Foundation CaptureEngine — additive OK)
'   ✅ No reflection in production path
'   ✅ No TryCast hack (DirectCast is type-safe at compile time)
'   ✅ Frame ownership = BORROW (encoder MUST NOT dispose texture)
'   ✅ No backward dependency from Video.Ddagrab → Encoder.Nvenc
'   ✅ No circular project reference

Namespace CaptureEngine.Video

    ''' <summary>
    ''' Extension of IVideoFrame that exposes the D3D11 texture pointer.
    '''
    ''' D3D11-producing backends (DdagrabBackend) emit frames implementing
    ''' BOTH IVideoFrame AND ID3D11VideoFrame. The encoder casts to this
    ''' interface to access the native texture without reflection.
    '''
    ''' Ownership: the texture is BORROWED. The encoder MUST NOT dispose it.
    ''' The frame owner (DdagrabBackend) disposes the texture when the
    ''' frame is disposed.
    ''' </summary>
    Public Interface ID3D11VideoFrame
        Inherits IVideoFrame

        ''' <summary>
        ''' The D3D11 texture object (boxed as Object to avoid leaking Vortice
        ''' into the CaptureEngine.Video contract). The encoder casts this
        ''' back to ID3D11Texture2D via DirectCast.
        '''
        ''' Returns Nothing if the frame has been disposed (defensive).
        ''' </summary>
        ReadOnly Property NativeTexture As Object

        ''' <summary>
        ''' Shared NT handle for cross-device resource sharing.
        ''' IntPtr.Zero = direct path (NativeTexture used directly).
        ''' Non-zero = shared-handle path (encoder opens via OpenSharedResource1).
        '''
        ''' Phase 12a-5c: validates D3D11 shared-resource contract.
        ''' </summary>
        ReadOnly Property SharedHandle As IntPtr

    End Interface

End Namespace
