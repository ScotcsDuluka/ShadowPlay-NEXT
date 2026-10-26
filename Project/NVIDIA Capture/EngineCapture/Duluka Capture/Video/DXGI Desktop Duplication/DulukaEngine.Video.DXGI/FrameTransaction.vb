Option Strict On
Option Explicit On

Imports System.Threading
Imports System.Runtime.CompilerServices

' Isolated fake-frame tests may call the Friend helpers directly, including
' ByRef failure paths (Reflection.Invoke cannot reliably copy them on throw).
<Assembly: InternalsVisibleTo("I3DFrame.Tests")>

Namespace DulukaEngine.Video.Backends.Ddagrab
    ' These helpers are shared by the real worker and device-free fault fixtures.
    ' There is no capture fallback or alternate device implementation here.
    Friend NotInheritable Class FrameTransaction
        Friend Shared Function Execute(acquire As Func(Of Boolean), build As Func(Of IVideoFrame),
                                       release As Action, push As Func(Of IVideoFrame, PushOutcome)) As Boolean
            If acquire Is Nothing Then Throw New ArgumentNullException(NameOf(acquire))
            If build Is Nothing Then Throw New ArgumentNullException(NameOf(build))
            If release Is Nothing Then Throw New ArgumentNullException(NameOf(release))
            If push Is Nothing Then Throw New ArgumentNullException(NameOf(push))
            ' Acquire delegates MUST return True immediately after acquisition succeeds;
            ' there must be no throwing work between the native success and this return.
            ' All QI/timestamp/Copy/cursor/construct work belongs in build().
            Dim acquired As Boolean
            Dim frame As IVideoFrame = Nothing
            Try
                Try
                    acquired = acquire()
                    If Not acquired Then Return False
                    frame = build()
                    If frame Is Nothing Then Throw New SourceFailureException(SourceFaultKind.InvalidFrame, "source_missing_owned_frame")
                Finally
                    ' Immediate guard: no timestamp, QI, logging, allocation or callback
                    ' occurs after successful acquire and before this Finally is armed.
                    If acquired Then release()
                End Try
                ' The duplication frame is already released; only an owned copy remains.
                Select Case push(frame)
                    Case PushOutcome.Pushed, PushOutcome.Replaced
                        frame = Nothing
                    Case PushOutcome.Dropped
                        ' Producer still owns it.
                    Case Else
                        Throw New SourceFailureException(SourceFaultKind.InvalidFrame, "source_unknown_push_outcome")
                End Select
                Return True
            Finally
                frame?.Dispose()
            End Try
        End Function
    End Class

    Friend NotInheritable Class OwnedFrameBuilder
        Friend Shared Function Build(Of T As {Class, IDisposable})(createTexture As Func(Of Boolean, T),
            prepare As Action(Of T), copyShared As Action(Of T, T), useShared As Boolean,
            createHandle As Func(Of T, IntPtr), closeHandle As Action(Of IntPtr),
            construct As Func(Of T, IntPtr, IVideoFrame), released As Action) As IVideoFrame
            If createTexture Is Nothing Then Throw New ArgumentNullException(NameOf(createTexture))
            If prepare Is Nothing Then Throw New ArgumentNullException(NameOf(prepare))
            If copyShared Is Nothing Then Throw New ArgumentNullException(NameOf(copyShared))
            If construct Is Nothing Then Throw New ArgumentNullException(NameOf(construct))
            If useShared AndAlso createHandle Is Nothing Then Throw New ArgumentNullException(NameOf(createHandle))
            If useShared AndAlso closeHandle Is Nothing Then Throw New ArgumentNullException(NameOf(closeHandle))
            Using staging = New ResourceOwner(Of T)(Nothing, released),
                  sharedOwner = New ResourceOwner(Of T)(Nothing, released), handle = New HandleOwner(closeHandle)
                staging.SetValue(createTexture(False))
                prepare(staging.Value)
                Dim owner = staging
                If useShared Then
                    sharedOwner.SetValue(createTexture(True))
                    copyShared(sharedOwner.Value, staging.Value)
                    handle.Value = createHandle(sharedOwner.Value)
                    If handle.Value = IntPtr.Zero Then Throw New InvalidOperationException("source_shared_handle_missing")
                    owner = sharedOwner
                End If
                Dim frame = construct(owner.Value, handle.Value)
                If frame Is Nothing Then Throw New InvalidOperationException("source_frame_construction_returned_null")
                owner.Detach()
                handle.Detach()
                Return frame
            End Using
        End Function
    End Class

    Friend NotInheritable Class ResourceOwner(Of T As {Class, IDisposable})
        Implements IDisposable
        Private _value As T
        Private ReadOnly _released As Action
        Friend Sub New(value As T, Optional released As Action = Nothing)
            _value = value
            _released = released
        End Sub
        Friend ReadOnly Property Value As T
            Get
                Return _value
            End Get
        End Property
        Friend Function Detach() As T
            Return Interlocked.Exchange(_value, Nothing)
        End Function
        Friend Sub SetValue(value As T)
            If _value IsNot Nothing Then Throw New InvalidOperationException("resource_already_owned")
            If value Is Nothing Then Throw New InvalidOperationException("source_owned_texture_missing")
            _value = value
        End Sub
        Public Sub Dispose() Implements IDisposable.Dispose
            Dim value = Detach()
            If value Is Nothing Then Return
            Try
                value.Dispose()
            Catch
                ' Teardown must continue through the remaining owners.
            Finally
                Try : _released?.Invoke() : Catch : End Try
            End Try
        End Sub
    End Class

    Friend NotInheritable Class HandleOwner
        Implements IDisposable
        Private _value As IntPtr
        Private ReadOnly _close As Action(Of IntPtr)
        Friend Sub New(close As Action(Of IntPtr))
            _close = close
        End Sub
        Friend Property Value As IntPtr
            Get
                Return _value
            End Get
            Set(value As IntPtr)
                If _value <> IntPtr.Zero Then Throw New InvalidOperationException("handle_already_owned")
                _value = value
            End Set
        End Property
        Friend Function Detach() As IntPtr
            Return Interlocked.Exchange(_value, IntPtr.Zero)
        End Function
        Public Sub Dispose() Implements IDisposable.Dispose
            Dim value = Detach()
            If value <> IntPtr.Zero Then
                Try : _close(value) : Catch : End Try
            End If
        End Sub
    End Class

    Friend NotInheritable Class SourceFailureException
        Inherits Exception
        Friend ReadOnly Kind As SourceFaultKind
        Friend Sub New(kind As SourceFaultKind, message As String, Optional inner As Exception = Nothing)
            MyBase.New(message, inner)
            Me.Kind = kind
        End Sub
    End Class

    Friend NotInheritable Class DuplicationReplacement
        Friend Shared Sub Replace(Of T As {Class, IDisposable}, TScope As {Class, IDisposable})(
            ByRef current As T, createScope As Func(Of TScope), create As Func(Of TScope, T))
            If createScope Is Nothing Then Throw New ArgumentNullException(NameOf(createScope))
            If create Is Nothing Then Throw New ArgumentNullException(NameOf(create))
            Dim old = current
            current = Nothing ' Never leave a disposed wrapper observable after failure.
            old?.Dispose()
            Dim candidate As T = Nothing
            Try
                Using scope = createScope()
                    candidate = create(scope)
                End Using
                If candidate Is Nothing Then Throw New InvalidOperationException("duplication_creation_returned_null")
                current = candidate
                candidate = Nothing
            Finally
                candidate?.Dispose()
            End Try
        End Sub
    End Class
End Namespace
