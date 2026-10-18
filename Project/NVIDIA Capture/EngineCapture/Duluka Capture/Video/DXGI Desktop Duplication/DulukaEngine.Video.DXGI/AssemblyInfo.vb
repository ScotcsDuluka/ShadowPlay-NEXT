Option Strict On
Option Explicit On

Imports System.Runtime.CompilerServices

' Expose Friend (internal) members of DulukaEngine.Video.Ddagrab to the
' test assembly so tests can observe internal backend state (e.g.
' CurrentState) without exposing them on the public API surface.
<Assembly: InternalsVisibleTo("DulukaEngine.Video.Tests")>
<Assembly: InternalsVisibleTo("NVIDIA.Soak")>
