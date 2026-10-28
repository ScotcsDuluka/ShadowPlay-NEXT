#requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$RunSmoke,
    [switch]$ConfirmPrivacyCleared,
    [switch]$ConfirmAudioDisabled,
    [int]$Port = 59477,
    [int]$Seconds = 3,
    [ValidateSet('FFmpeg', 'Duluka')]
    [string]$Engine = 'FFmpeg',
    [string]$ExecutablePath = '',
    [string]$IsolatedRoot = '',
    [string]$EvidenceDirectory = ''
)
$ErrorActionPreference = 'Stop'
# Record modes, including FFmpeg/gdigrab, read ACTUAL desktop pixels.
# Only explicit double confirmation can cross this boundary.
# The default prepare-only mode is safe and does not start a worker.
if ($RunSmoke) {
    if (-not $ConfirmPrivacyCleared) {
        throw 'LIVE_CAPTURE_REFUSED: use -ConfirmPrivacyCleared only after placing a neutral, privacy-cleared test scene on the selected display'
    }
    if (-not $ConfirmAudioDisabled) {
        throw 'LIVE_CAPTURE_REFUSED: use -ConfirmAudioDisabled only after confirming both system audio and microphone capture are off'
    }
    if ($Seconds -lt 2 -or $Seconds -gt 15) {
        throw 'LIVE_CAPTURE_REFUSED: -Seconds must be between 2 and 15 for a bounded test'
    }
}

# Run an explicitly staged build or the source output, never an installed EXE.
$capture = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = (Resolve-Path (Join-Path $capture '..')).Path
$bin = Join-Path $capture 'bin\Release\net10.0-windows10.0.26100.0'
$exe = Join-Path $bin 'NvCapture.exe'
if ($ExecutablePath) {
    $exe = [IO.Path]::GetFullPath($ExecutablePath)
    $bin = Split-Path -Parent $exe
}
$ffmpeg = Join-Path $project 'FFmpeg\ffmpeg.exe'
$probe = Join-Path $project 'FFmpeg\ffprobe.exe'
# Per-port isolated root prevents simultaneous tests from rewriting one
# another's config or claiming another test worker's output files.
$root = Join-Path $capture ('bin\Release\IsolatedCaptureRoot-' + $Port)
if ($IsolatedRoot) { $root = [IO.Path]::GetFullPath($IsolatedRoot) }
$marker = Join-Path $root '.shadowplay-capture-isolated'
$records = Join-Path $root 'Records'
if ($Port -lt 59100 -or $Port -gt 59999) { throw 'Only isolated REST ports 59100-59999 are allowed.' }
if (-not (Test-Path -LiteralPath $exe)) { throw "Build feature NvCapture first: $exe" }
if (-not (Test-Path -LiteralPath $ffmpeg)) { throw 'Source FFmpeg not found' }
if ((Test-Path -LiteralPath $root) -and -not (Test-Path -LiteralPath $marker)) {
    throw 'Existing root without isolated marker; refusing to touch it'
}
# Refuse to rewrite configuration while another test worker owns the port.
$existing = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
if ($existing) { throw "Test port $Port already belongs to process $($existing[0].OwningProcess)" }
# Both FFmpeg/gdigrab and Duluka/native use real desktop pixels.
# Fail CLOSED if production health is unavailable or not verifiably idle.
if ($RunSmoke) {
    try {
        $stable = Invoke-RestMethod -Uri 'http://127.0.0.1:59077/health' -TimeoutSec 3
    } catch {
        throw 'LIVE_CAPTURE_REFUSED: production NvCapture health unavailable'
    }
    if ($stable.ok -ne $true -or $stable.ready -ne $true -or
        $stable.recording -ne $false) {
        throw 'LIVE_CAPTURE_REFUSED: production capture is not verifiably idle'
    }
}
New-Item -ItemType Directory -Force -Path (Join-Path $root 'Config'), $records | Out-Null
Set-Content -LiteralPath $marker -Value 'shadowplay-next-isolated-capture-v1' -NoNewline -Encoding Ascii

$config = @{
    Recording = @{
        engine_mode = $Engine
        api_capture = $(if($Engine -eq 'Duluka'){'dxgi_desktop_duplication'}else{'gdigrab'})
        encoder = $(if($Engine -eq 'Duluka'){'NVENC_H264'}else{'LIBX264'})
        encoder_now = $(if($Engine -eq 'Duluka'){'NVENC_H264'}else{'LIBX264'})
        current = @{
            fps = 30
            bitrate = 4000
            encoder_preset = 4
            use_native_resolution = $true
            width = 1280
            height = 720
        }
    }
    Paths = @{
        FFmpegPath = $ffmpeg
        GalleryPath = $records
        SavePath = $records
    }
    Audio = @{
        SystemAudioEnabled = $false
        MicEnabled = $false
    }
}
$engineConfig = @{
    Encoder = $(if($Engine -eq 'Duluka'){'NVENC_H264'}else{'libx264'})
    CaptureMethod = $(if($Engine -eq 'Duluka'){'dxgi_desktop_duplication'}else{'gdigrab'})
    FPS = 30
    Bitrate = 4000000
    FFmpegPath = $ffmpeg
    OutputDirectory = $records
    SystemAudioCapture = $false
    MicCapture = $false
}
[IO.File]::WriteAllText((Join-Path $root 'Config\config.json'), ($config | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $root 'Config\engine.json'), ($engineConfig | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Host "Isolated test config: $root"
Write-Host "REST port: $Port"
if (-not $RunSmoke) {
    Write-Host 'Prepare-only mode. No NvCapture process or recording started.'
    exit 0
}
$secretPath = Join-Path $env:LOCALAPPDATA 'ShadowPlay NEXT\Secrets\pairing-current-user.dpapi'
if (-not (Test-Path -LiteralPath $secretPath -PathType Leaf)) {
    throw 'Protected Capture authorization is not enrolled; smoke cannot mutate the worker.'
}
Add-Type -AssemblyName System.Security
$clearSecret = [Security.Cryptography.ProtectedData]::Unprotect(
    [IO.File]::ReadAllBytes($secretPath), $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
try { $token = [Text.Encoding]::ASCII.GetString($clearSecret) }
finally { [Array]::Clear($clearSecret, 0, $clearSecret.Length) }
if ($token -notmatch '^[0-9a-fA-F]{32}$') { throw 'Protected Capture authorization has invalid format.' }
$writeHeaders = @{ 'X-ShadowPlay-Capture-Token' = $token }
if ($EvidenceDirectory) { New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null }
$psi = New-Object Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.Arguments = '--capture-sandbox --rest'
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.WorkingDirectory = $bin
$psi.EnvironmentVariables['NVIDIA_SHADOWPLAY_APP_ROOT'] = $root
$psi.EnvironmentVariables['NV_CAPTURE_ISOLATED_ROOT'] = $root
$psi.EnvironmentVariables['NV_CAPTURE_ISOLATED_REST_PORT'] = [string]$Port

$child = [Diagnostics.Process]::Start($psi)
if (-not $child) { throw 'Isolated NvCapture failed to launch' }
$base = "http://127.0.0.1:$Port"
try {
    $ready = $false
    for ($i=0; $i -lt 35; $i++) {
        if ($child.HasExited) { throw "Isolated process exited before health: $($child.ExitCode)" }
        try {
            $health = Invoke-RestMethod -Uri "$base/health" -TimeoutSec 2
            if ($health.ok -and ($Engine -ne 'Duluka' -or $health.ready)) { $ready = $true; break }
        } catch {}
        Start-Sleep -Milliseconds 500
    }
    if (-not $ready) {
        $lastState = ''
        try { $lastState = (Invoke-RestMethod -Uri "$base/state" -TimeoutSec 3 | ConvertTo-Json -Compress) } catch {}
        throw "Isolated NvCapture not ready: $lastState"
    }
    $engines = Invoke-RestMethod -Uri "$base/engines" -TimeoutSec 8
    if ($EvidenceDirectory) { $engines | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'engines-before.json') -Encoding UTF8 }
    if ($engines.configured.backend -ne $Engine) { throw "Wrong engine: $($engines.configured.backend)" }
    Write-Host "Isolated PID=$($child.Id) route=$($engines.configured.backend)"
    $started = Invoke-RestMethod -Uri "$base/record/start" -Method POST -Headers $writeHeaders -ContentType 'application/json' -Body '{}' -TimeoutSec 30
    if (-not $started.started) { throw "Start rejected: $($started.error) $($started.probeReason)" }
    if ($started.backend -ne $Engine) { throw "Incorrect actual backend $($started.backend)" }
    Write-Host "Start: backend=$($started.backend)"
    Start-Sleep -Seconds $Seconds
    $st = Invoke-RestMethod -Uri "$base/state" -TimeoutSec 5
    if ($EvidenceDirectory) { $st | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'state-active.json') -Encoding UTF8 }
    if (-not $st.recording -or $st.actualSessionBackend -ne $Engine) {
        throw 'Recorder lost its owned session'
    }
    if ($Engine -eq 'Duluka' -and (-not $st.isCapturing -or $st.actualRunningEngine -ne 'Duluka' -or
        $st.runtimeTruth.realFramesCaptured -lt 1 -or $st.runtimeTruth.framesEncoded -lt 1)) {
        throw 'Native runtime did not prove real frame and encoded progress.'
    }
    $stopped = Invoke-RestMethod -Uri "$base/record/stop" -Method POST -Headers $writeHeaders -ContentType 'application/json' -Body '{}' -TimeoutSec 45
    if ($EvidenceDirectory) { $stopped | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'stop-result.json') -Encoding UTF8 }
    if (-not $stopped.stopped) { throw "Stop not confirmed: $($stopped.error)" }
    if (-not $stopped.saved -or $stopped.finalizationState -ne 'Saved' -or -not $stopped.mediaVerified) {
        throw "Session was not verified Saved: $($stopped.finalizationState) $($stopped.error)"
    }
    if (-not $stopped.media -or -not $stopped.media.verified -or
        $stopped.media.videoStreams -lt 1) {
        throw "Final MP4 media evidence missing: $($stopped.media.reason)"
    }
    if ($stopped.media.audioStreams -ne 0) {
        throw "Video-only smoke unexpectedly contained $($stopped.media.audioStreams) audio tracks"
    }
    if ($stopped.systemAudioRequestedAtStart -or $stopped.microphoneRequestedAtStart) {
        throw 'Video-only test unexpectedly requested actual audio devices'
    }
    Write-Host "Media evidence: video=$($stopped.media.videoStreams) audio=$($stopped.media.audioStreams) duration=$($stopped.media.durationSec)s"
    if ([double]$stopped.media.durationSec -lt ([double]$Seconds - 0.75)) {
        throw "Finalized duration $($stopped.media.durationSec)s is too short for requested $($Seconds)s recording window"
    }
    $target = [IO.Path]::GetFullPath([string]$stopped.file)
    $recordPrefix = [IO.Path]::GetFullPath($records).TrimEnd([char]92) + [char]92
    if (-not $target.StartsWith($recordPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output escaped isolated root: $target"
    }
    if (-not (Test-Path -LiteralPath $target)) { throw "Finalized file missing: $target" }
    $size = (Get-Item -LiteralPath $target).Length
    if ($size -lt 10000) { throw "Recording output too small: $size" }
    if (-not (Test-Path -LiteralPath $probe)) { throw 'ffprobe not found' }
    $metadata = & $probe -v error -show_entries stream=codec_name,width,height -show_entries format=duration -of default=noprint_wrappers=1 "$target"
    if ($LASTEXITCODE -ne 0 -or -not ($metadata -match 'codec_name=h264')) {
        throw 'FFprobe could not verify H.264 output'
    }
    $decodeLog = Join-Path $root 'full-decode.log'
    $decode = New-Object Diagnostics.ProcessStartInfo
    $decode.FileName = $ffmpeg
    $decode.Arguments = '-nostdin -hide_banner -loglevel error -xerror -i "' + $target + '" -map 0:v:0 -f null -'
    $decode.UseShellExecute = $false
    $decode.CreateNoWindow = $true
    $decode.RedirectStandardError = $true
    $decoder = [Diagnostics.Process]::Start($decode)
    try {
        $errorRead = $decoder.StandardError.ReadToEndAsync()
        if (-not $decoder.WaitForExit(30000)) { $decoder.Kill(); throw 'Full video decode exceeded 30 seconds.' }
        $decodeErrors = $errorRead.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($decodeLog, $decodeErrors)
        if ($decoder.ExitCode -ne 0 -or $decodeErrors.Trim().Length -gt 0) { throw 'Full video decode failed.' }
    } finally { $decoder.Dispose() }
    if ($EvidenceDirectory) {
        @{ engine=$Engine; executable=$exe; executableSha256=(Get-FileHash -LiteralPath $exe).Hash;
           start=$started; active=$st; stop=$stopped; file=$target; bytes=$size;
           fullDecodePassed=$true; audioDevicesRequested=$false } |
            ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory ($Engine.ToLowerInvariant()+'-desktop-smoke.json')) -Encoding UTF8
    }
    Write-Host "SMOKE_PASS file=$target bytes=$size"
    Write-Output $metadata
}
finally {
    if (-not $child.HasExited) {
        # Only terminate the child created by THIS script.
        try { $child.Kill(); $child.WaitForExit(4000) | Out-Null } catch {}
    }
    $child.Dispose()
    $writeHeaders.Clear()
    $token = $null
}
