Option Strict On
Option Explicit On

Imports System.Runtime.CompilerServices

' Expose Friend (internal) members of CaptureEngine.Encoder to the test
' assembly so tests can observe internal stop-path invocations (state
' transitions, counter increments) without exposing them on the public
' API surface.
<Assembly: InternalsVisibleTo("CaptureEngine.Encoder.Tests")>
