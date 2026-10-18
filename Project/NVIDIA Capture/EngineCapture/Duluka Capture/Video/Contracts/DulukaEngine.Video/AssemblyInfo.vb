Option Strict On
Option Explicit On

Imports System.Runtime.CompilerServices

' Expose Friend (internal) members of DulukaEngine.Video to the test assembly
' so tests can observe internal state (e.g. backend's internal worker counters)
' without exposing them on the public API surface.
<Assembly: InternalsVisibleTo("DulukaEngine.Video.Tests")>
