# Checks live FFmpeg/NVENC verification using synthetic color frames only.
# No screen capture, recording, or media output files.
$ErrorActionPreference = 'Stop'
$tests = 0
function Confirm($ok, $name) {
    if (-not $ok) { throw ('FAIL: ' + $name) }
    $script:tests++
    Write-Output ('PASS: ' + $name)
}
$base = 'http://127.0.0.1:59077'
$health = Invoke-RestMethod "$base/health" -TimeoutSec 5
Confirm ($health.ok -and -not $health.recording) 'Capture ready; not recording'
$binary = (Invoke-RestMethod "$base/engines" -TimeoutSec 5).ffmpegBinary
# First GET may legitimately return probe_pending after Container restarts NvCapture.
for ($attempt = 0; $attempt -lt 30 -and -not $binary.binaryRunnable -and
     $binary.reason -eq "ffmpeg_probe_pending"; $attempt++) {
    Start-Sleep -Milliseconds 500
    $binary = (Invoke-RestMethod "$base/engines" -TimeoutSec 5).ffmpegBinary
}
Confirm ($binary.binaryRunnable -and $binary.encoders.Count -ge 1) 'Runnable FFmpeg advertises encoders'
$state = $binary.nvencSyntheticValidation
if ($state.status -ne 'complete') {
    $post = Invoke-RestMethod "$base/encoders/verify" -Method POST -ContentType 'application/json' -Body '{}' -TimeoutSec 8
    Confirm ($post.started -or $post.reason -in @('already_running','rate_limited')) 'Explicit synthetic test accepted/already active'
    foreach ($index in 1..50) {
        Start-Sleep -Milliseconds 400
        $state = (Invoke-RestMethod "$base/engines" -TimeoutSec 5).ffmpegBinary.nvencSyntheticValidation
        if ($state.status -in @('complete','error')) { break }
    }
}
Confirm ($state.status -eq 'complete') 'Hardware probe completed'
Confirm ($state.gpuCount -gt 0 -and $state.results.Count -gt 0) 'Per-GPU test results populated'
foreach ($sample in $state.results) {
    Confirm (-not $sample.realRecordingVerified) ('No false recording claim: ' + $sample.gpuIndex + '/' + $sample.encoder)
    Confirm ($sample.syntheticEncodePassed -is [bool]) ('Explicit synthetic outcome: ' + $sample.gpuIndex + '/' + $sample.encoder)
}
$after = (Invoke-RestMethod "$base/engines" -TimeoutSec 5).ffmpegBinary.nvencSyntheticValidation
Confirm ($state.testedAtUtc -eq $after.testedAtUtc) 'GET status did not rerun GPU test'
$container = (Invoke-RestMethod 'http://127.0.0.1:59012/v1/capture/engines' -TimeoutSec 5).workerRuntime.ffmpegBinary.nvencSyntheticValidation
$node = (Invoke-RestMethod 'http://127.0.0.1:59011/DulukaCapture/v.1.0/engines' -TimeoutSec 5).workerRuntime.ffmpegBinary.nvencSyntheticValidation
Confirm ($container.results.Count -eq $state.results.Count) 'Container forwards exact result count'
Confirm ($node.results.Count -eq $state.results.Count) 'NodeAPI forwards exact result count'
Write-Output ('SUCCESS: ' + $tests + ' contract checks; GPUs=' + $state.gpuCount)
