Option Strict On
Option Explicit On

Imports System

Namespace CaptureEngine.Encoder
    ''' <summary>
    ''' Base exception type for encoder-related failures. (P1-F §7)
    '''
    ''' Hierarchy:
    '''   EncoderException (base)
    '''     ├── EncoderConfigurationException  — invalid config at Initialize()
    '''     ├── EncoderRuntimeException       — runtime failure during Encode()
    '''     └── EncoderShutdownException      — Flush/Stop timeout
    '''
    ''' Mirrors the Video Backend exception pattern (VideoBackendException etc.)
    ''' so callers can write uniform try/catch patterns across backends.
    ''' </summary>
    Public Class EncoderException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

        Public Sub New(message As String, innerException As Exception)
            MyBase.New(message, innerException)
        End Sub
    End Class

    ''' <summary>
    ''' Thrown when an EncoderConfig value is invalid (e.g. unknown CodecKey,
    ''' BitrateBps <= 0 with RateControl=cbr, GopSize <= 0).
    ''' </summary>
    Public Class EncoderConfigurationException
        Inherits EncoderException

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

        Public Sub New(message As String, innerException As Exception)
            MyBase.New(message, innerException)
        End Sub
    End Class

    ''' <summary>
    ''' Thrown when the encoder encounters a runtime failure (e.g. codec
    ''' error, frame dimension mismatch, GPU device lost). Transitions
    ''' the encoder to Faulted state.
    ''' </summary>
    Public Class EncoderRuntimeException
        Inherits EncoderException

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

        Public Sub New(message As String, innerException As Exception)
            MyBase.New(message, innerException)
        End Sub
    End Class

    ''' <summary>
    ''' Thrown when Flush() or Stop() does not complete within the
    ''' configured timeout. The encoder is in an indeterminate state —
    ''' caller MUST Dispose() it.
    ''' </summary>
    Public Class EncoderShutdownException
        Inherits EncoderException

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub

        Public Sub New(message As String, innerException As Exception)
            MyBase.New(message, innerException)
        End Sub
    End Class
End Namespace
