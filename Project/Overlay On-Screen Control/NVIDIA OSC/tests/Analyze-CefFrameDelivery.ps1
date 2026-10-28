[CmdletBinding()]
param([Parameter(Mandatory)][string]$CaptureDirectory,
      [ValidatePattern('^[A-Za-z0-9_-]+\.json$')][string]$SummaryName='delivery-summary.json')
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath $CaptureDirectory).Path
$output=Join-Path $root $SummaryName
if(Test-Path -LiteralPath $output){throw 'Refusing to overwrite delivery analysis'}
$phases=@(Import-Csv -LiteralPath (Join-Path $root 'phases.csv'))
$samples=@(Import-Csv -LiteralPath (Join-Path $root 'callbacks.csv'))
$results=@()
function Percentile($sorted,[double]$p) {
    if(-not $sorted.Count){return $null}
    return $sorted[[int][Math]::Ceiling($sorted.Count*$p)-1]
}
foreach($phase in $phases) {
    $frequency=[double]$phase.frequency
    $start=[decimal]$phase.start_qpc; $end=[decimal]$phase.end_qpc
    $all=@($samples|Where-Object {$_.phase -eq $phase.phase})
    $gpu=@($all|Where-Object {[int]$_.gpu -eq 1}|Sort-Object {[decimal]$_.qpc})
    $inWindow=@($gpu|Where-Object {[decimal]$_.qpc -ge $start -and [decimal]$_.qpc -le $end})
    $duration=if($frequency -gt 0){[double](($end-$start)/[decimal]$frequency)}else{0.0}
    $intervals=@(for($i=1;$i -lt $inWindow.Count;$i++){
        [double](([decimal]$inWindow[$i].qpc-[decimal]$inWindow[$i-1].qpc)*1000/[decimal]$frequency)
    })
    $sorted=@($intervals|Sort-Object)
    $actual=if($duration -gt 0){$inWindow.Count/$duration}else{0.0}
    $ratio=$actual/[double]$phase.requested
    $invalid=@($gpu|Where-Object {[int]$_.handle_valid -ne 1}).Count
    $consistent=([int]$phase.gpu_callbacks -eq $gpu.Count) -and
        ([int]$phase.cpu_callbacks -eq ($all.Count-$gpu.Count))
    $quality=$duration -gt 0 -and $frequency -gt 0 -and $consistent -and
        [int]$phase.storage_overflow -eq 0 -and $invalid -eq 0 -and
        [int]$phase.cpu_callbacks -eq 0 -and $inWindow.Count -gt 1
    $pixels=@($inWindow|Where-Object {$_.pixel_read -eq '1'})
    $unique=0; $backwards=0; $previous=$null
    $uniqueQpcs=[Collections.Generic.List[decimal]]::new()
    foreach($pixel in $pixels) {
        $serial=[UInt32]$pixel.pixel_serial
        if($null -eq $previous -or $serial -ne $previous){$unique++;$uniqueQpcs.Add([decimal]$pixel.qpc)}
        if($null -ne $previous -and $serial -lt $previous){$backwards++}
        $previous=$serial
    }
    $pixelQuality=$quality -and $pixels.Count -eq $inWindow.Count -and $backwards -eq 0
    $uniqueRate=if($duration -gt 0){$unique/$duration}else{0.0}
    $uniqueRatio=$uniqueRate/[double]$phase.requested
    $uniqueIntervals=@(for($i=1;$i -lt $uniqueQpcs.Count;$i++){
        [double](($uniqueQpcs[$i]-$uniqueQpcs[$i-1])*1000/[decimal]$frequency)
    })
    $uniqueSorted=@($uniqueIntervals|Sort-Object)
    $uniqueTiming=$null
    if($pixelQuality -and $uniqueSorted.Count -gt 0) {
        $mean=($uniqueSorted|Measure-Object -Average).Average
        $slowCount=[int][Math]::Ceiling($uniqueSorted.Count*0.01)
        $slowMean=($uniqueSorted|Select-Object -Last $slowCount|Measure-Object -Average).Average
        $uniqueTiming=[ordered]@{
            averageFps=1000.0/$mean; onePercentLowFps=1000.0/$slowMean
            p50Ms=(Percentile $uniqueSorted 0.50); p95Ms=(Percentile $uniqueSorted 0.95)
            p99Ms=(Percentile $uniqueSorted 0.99); maxMs=$uniqueSorted[-1]
        }
    }
    $results+=[pscustomobject]@{
        label=$phase.label; requestedFps=[int]$phase.requested; legacyGetterFps=[int]$phase.getter
        durationSeconds=$duration; gpuCallbacks=$gpu.Count; cpuCallbacks=[int]$phase.cpu_callbacks
        boundaryExcluded=$gpu.Count-$inWindow.Count; invalidHandles=$invalid
        observedGpuCallbacksPerSecond=$actual; deliveredToRequestedRatio=$ratio
        p50CallbackIntervalMs=(Percentile $sorted 0.50)
        p95CallbackIntervalMs=(Percentile $sorted 0.95)
        p99CallbackIntervalMs=(Percentile $sorted 0.99)
        maxCallbackIntervalMs=if($sorted.Count){$sorted[-1]}else{$null}
        dataQuality=if($quality){'PASS'}else{'UNVERIFIED'}
        targetWithinFivePercent=if(-not $quality){'UNVERIFIED'}elseif($ratio -ge 0.95 -and $ratio -le 1.05){'PASS'}else{'FAIL'}
        pixelSamples=$pixels.Count; uniquePixelSerials=$unique; repeatedPixelCallbacks=$pixels.Count-$unique
        backwardsPixelSerials=$backwards
        observedUniquePixelFramesPerSecond=if($pixelQuality){$uniqueRate}else{$null}
        pixelSerialSpan=if($pixels.Count){[UInt32]$pixels[-1].pixel_serial-[UInt32]$pixels[0].pixel_serial}else{$null}
        pixelDataQuality=if($pixelQuality){'PASS'}else{'UNVERIFIED'}
        uniqueFrameTargetWithinFivePercent=if(-not $pixelQuality){'UNVERIFIED'}elseif($uniqueRatio -ge 0.95 -and $uniqueRatio -le 1.05){'PASS'}else{'FAIL'}
        uniquePixelTransitionTiming=$uniqueTiming
    }
}
[ordered]@{
    scope='Isolated actual CEF 73 accelerated callbacks on an animated 640x360 local page; no game, Hook or OSC compositor.'
    criterion='Observed callbacks/actual QPC window within +/-5% of requested FPS. This is an explicit probe criterion, not a CEF guarantee.'
    limitation='With full pixel coverage, unique frames are identified by the animated page RGB serial copied from the actual CEF GPU texture. Otherwise unique frames remain UNVERIFIED. No displayed frames, OSC Present, full Host boost-task-chain or game FPS was measured.'
    analyzerSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    results=$results
}|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $output -Encoding utf8
Write-Output $output
