Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text.RegularExpressions

' Host-side config — the SAME resolution order as the backend's config.js
' (defaults < config.json < environment), so the host's guard/health probes
' and the node child always agree on one port. Per the proven launch
' contract (docs/PHASE-1B-LAUNCH-CONTRACT.md §5): JSON + environment ONLY —
' no registry, no argv.
'
'   port     : NVSP_PORT env > config.json "port" > 59011 (shared with
'              NvContainer and the native OSC host)
'   bindHost : NVSP_HOST env > config.json "host" > 127.0.0.1 (loopback —
'              the osc frontend and the engine run on this machine)
Friend Class HostConfig

    Public Property Port As Integer = 59011
    Public Property BindHost As String = "127.0.0.1"
    Public Property HealthPath As String = "/Backend/v.1.0/health"

    Public ReadOnly Property BaseUrl As String
        Get
            Return "http://" & BindHost & ":" & Port.ToString(Globalization.CultureInfo.InvariantCulture)
        End Get
    End Property

    Public Shared Function Load(baseDir As String) As HostConfig
        Dim cfg As New HostConfig()

        ' config.json beside index.js (the backend's own config file — one
        ' source of truth for both processes; a missing file is normal)
        Dim configPath = Path.Combine(baseDir, "config.json")
        Try
            If File.Exists(configPath) Then
                Dim json = ReadFlatJson(File.ReadAllText(configPath))
                Dim raw As String = Nothing
                If json.TryGetValue("port", raw) Then
                    Dim p As Integer
                    If Integer.TryParse(raw, p) AndAlso p > 0 AndAlso p <= 65535 Then cfg.Port = p
                End If
                If json.TryGetValue("host", raw) AndAlso Not String.IsNullOrWhiteSpace(raw) Then
                    cfg.BindHost = raw.Trim()
                End If
            End If
        Catch ex As Exception
            HostLog.Warn("config.json unreadable (" & ex.Message & ") — defaults only")
        End Try

        ' environment wins over everything (backend/config.js contract)
        Dim envPort = Environment.GetEnvironmentVariable("NVSP_PORT")
        If Not String.IsNullOrWhiteSpace(envPort) Then
            Dim p As Integer
            If Integer.TryParse(envPort.Trim(), p) AndAlso p > 0 AndAlso p <= 65535 Then
                cfg.Port = p
            Else
                HostLog.Warn("NVSP_PORT='" & envPort & "' is not a valid port — keeping " & cfg.Port)
            End If
        End If
        Dim envHost = Environment.GetEnvironmentVariable("NVSP_HOST")
        If Not String.IsNullOrWhiteSpace(envHost) Then cfg.BindHost = envHost.Trim()

        Return cfg
    End Function

    ' Minimal flat {"key": value} reader — the host needs exactly the two
    ' scalar keys above and must not grow a JSON dependency (zero-dep rule).
    ' Anything nested or exotic simply yields no matches (fails safe to
    ' defaults). Mirrors how config.js reads its own flat file.
    Private Shared Function ReadFlatJson(text As String) As Dictionary(Of String, String)
        Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Dim scalar As New Regex("""(?<key>[^""]+)""\s*:\s*(?:""(?<str>[^""]*)""|(?<num>-?\d+))",
                                RegexOptions.Compiled)
        For Each m As Match In scalar.Matches(text)
            If m.Groups("num").Success Then
                result(m.Groups("key").Value) = m.Groups("num").Value
            Else
                result(m.Groups("key").Value) = m.Groups("str").Value
            End If
        Next
        Return result
    End Function

End Class
