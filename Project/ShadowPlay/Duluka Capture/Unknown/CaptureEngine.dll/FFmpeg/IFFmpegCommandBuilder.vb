Option Strict On
Option Explicit On
Option Infer On

Imports System
Imports System.Collections.Generic

Namespace CaptureEngine.FFmpeg
    ''' <summary>
    ''' Contract for building FFmpeg command-line arguments.
    '''
    ''' Implementations:
    '''   - FFmpegCommandBuilderV1 — wraps legacy CaptureEngine.BuildFFmpegArguments
    '''     (preserves byte-identical output for backward compatibility)
    '''   - FFmpegCommandBuilderV2 — reads from EngineConfigV2 (new unified schema)
    '''
    ''' Why an interface?
    '''   The legacy BuildFFmpegArguments is a Private method on CaptureEngine
    '''     that mixes V1 config reading with command construction.
    '''   V2 wants to:
    '''     1. Decouple command construction from CaptureEngine class
    '''     2. Allow snapshot testing (V1 vs V2 produce same string for same config)
    '''     3. Allow V2 to fix legacy bugs (bufsize, pix_fmt, etc.) without
    '''        touching the legacy code path
    '''
    ''' The interface is intentionally minimal — just Build(). Metadata (preset,
    ''' codec, capture method) is exposed via separate properties on the
    ''' concrete builders so tests can inspect what was chosen without
    ''' having to parse the resulting command string.
    '''
    ''' This interface lives in the CaptureEngine assembly (NOT in the Engine
    ''' WinForms exe) so that:
    '''   - Test projects can reference it without pulling in WinForms deps
    '''   - Future native backends (DXGI, WGC) can implement it from any assembly
    ''' </summary>
    Public Interface IFFmpegCommandBuilder

        ''' <summary>
        ''' Build the FFmpeg argument string for the given output file path.
        '''
        ''' The output file path is supplied at build time (not at construction)
        ''' because CaptureEngine.StartRecordingAsync may override the path
        ''' (e.g. when using two-process mode, the temp video path is
        ''' "&lt;base&gt;.video.tmp.mp4" instead of the final output path).
        '''
        ''' Implementations must:
        '''   - Quote file paths with double quotes (")
        '''   - Not append trailing whitespace
        '''   - Be deterministic for the same input config + output path
        ''' </summary>
        Function Build(outputFile As String) As String

        ''' <summary>
        ''' Human-readable label for diagnostics. Returns e.g.
        ''' "V1 (legacy CaptureEngine.BuildFFmpegArguments)" or
        ''' "V2 (EngineConfig v2 schema)".
        ''' </summary>
        ReadOnly Property BuilderLabel As String
    End Interface
End Namespace
