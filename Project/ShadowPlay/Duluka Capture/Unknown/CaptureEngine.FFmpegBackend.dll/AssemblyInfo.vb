Option Strict On
Option Explicit On

Imports System.Runtime.CompilerServices

' Expose Friend (internal) members of CaptureEngine.FFmpegBackend to the
' test assemblies so tests can observe internal state without exposing it
' on the public API surface.
<Assembly: InternalsVisibleTo("CaptureEngine.FFmpegTests")>
' C/5 dormant-stack isolation: FFmpegPipelineBackend / MuxCoordinator /
' FFmpegProcessHost / AudioSidecar are now Friend — Recording.Tests reaches
' MuxCoordinator (DualTrack / RuntimeSync tests) through this grant.
<Assembly: InternalsVisibleTo("CaptureEngine.Recording.Tests")>
