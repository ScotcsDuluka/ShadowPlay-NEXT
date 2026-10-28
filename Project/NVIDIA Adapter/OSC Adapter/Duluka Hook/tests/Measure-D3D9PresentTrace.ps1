# Narrow, external ETW capture; never launches, injects, focuses or changes a game.
# Metadata verified locally: Microsoft-Windows-D3D9, event 1 Present Start
# (pSwapchain, Flags), event 2 Present Stop (Result). Runtime Present cadence
# is not displayed-frame cadence, and excludes Hook work before runtime entry.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Capture','Analyze')][string]$Mode,
    [string]$SessionName,
    [string]$OutputDirectory,
    [ValidateRange(1,600)][int]$Seconds = 30,
    [ValidateRange(0,2147483647)][int]$GameProcessId = 0,
    [string]$EtlPath,
    # Supply only when independently known, e.g. from the Capture manifest.
    [UInt64]$PerformanceFrequency = 0
)
$ErrorActionPreference = 'Stop'
$providerGuid = '783aca0a-790e-4d7f-8451-aa850511c6b9'
$culture = [Globalization.CultureInfo]::InvariantCulture

function Invoke-RecordedTool([string]$File, [string[]]$Arguments, [string]$Stem) {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $File
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Could not start $File" }
    $outTask = $process.StandardOutput.ReadToEndAsync()
    $errTask = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $outTask.GetAwaiter().GetResult() | Set-Content -LiteralPath "$Stem.stdout.txt" -Encoding utf8
    $errTask.GetAwaiter().GetResult() | Set-Content -LiteralPath "$Stem.stderr.txt" -Encoding utf8
    $code = $process.ExitCode
    $process.Dispose()
    return $code
}

function New-EvidenceDirectory([string]$Path) {
    if (-not $Path) { throw 'OutputDirectory is required for Capture.' }
    $absolute = [IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $absolute) { throw "Refusing to overwrite evidence directory: $absolute" }
    [IO.Directory]::CreateDirectory($absolute) | Out-Null
    return $absolute
}

function Get-Field($Node, [string]$Name) {
    foreach ($item in $Node.SelectNodes('.//*')) {
        if ($item.LocalName -ieq $Name) { return ([string]$item.InnerText).Trim() }
        if ($item.LocalName -ieq 'Data' -and
            ($item.GetAttribute('Name') -ieq $Name -or $item.GetAttribute('name') -ieq $Name)) {
            return ([string]$item.InnerText).Trim()
        }
    }
    return $null
}

function Get-Metrics([object[]]$Values, [bool]$Cadence = $true) {
    if (-not $Values -or $Values.Count -eq 0) { return $null }
    $sorted = @($Values | ForEach-Object { [double]$_ } | Sort-Object)
    $sum = 0.0
    foreach ($value in $sorted) { $sum += $value }
    $slowCount = [int][Math]::Ceiling($sorted.Count * 0.01)
    $slowSum = 0.0
    for ($index = $sorted.Count - $slowCount; $index -lt $sorted.Count; $index++) { $slowSum += $sorted[$index] }
    return [ordered]@{
        count = $sorted.Count
        meanMs = $sum / $sorted.Count
        averageFps = $(if ($Cadence -and $sum -gt 0) { 1000.0 * $sorted.Count / $sum } else { $null })
        onePercentLowFps = $(if ($Cadence -and $slowSum -gt 0) { 1000.0 * $slowCount / $slowSum } else { $null })
        slowestOnePercentCount = $slowCount
        p50Ms = $sorted[[int][Math]::Ceiling(0.50 * $sorted.Count) - 1]
        p95Ms = $sorted[[int][Math]::Ceiling(0.95 * $sorted.Count) - 1]
        p99Ms = $sorted[[int][Math]::Ceiling(0.99 * $sorted.Count) - 1]
        maximumMs = $sorted[-1]
        summedMs = $sum
    }
}

if ($Mode -eq 'Capture') {
    if ($SessionName -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,95}$') { throw 'Use a fresh simple SessionName (letters, digits, underscore, hyphen).' }
    if ($GameProcessId -le 0) { throw 'Capture requires GameProcessId of the already-running game.' }
    $target = Get-Process -Id $GameProcessId -ErrorAction Stop
    $targetEvidence = [ordered]@{ processId = $GameProcessId; processName = $target.ProcessName; startTimeUtc = $target.StartTime.ToUniversalTime().ToString('o'); image = $target.Path }
    $directory = New-EvidenceDirectory $OutputDirectory
    $trace = Join-Path $directory 'd3d9-present.etl'
    $queryExit = Invoke-RecordedTool 'logman.exe' @('query',$SessionName,'-ets') (Join-Path $directory 'session-preflight')
    if ($queryExit -eq 0) { throw "ETW session already exists; refusing to touch it: $SessionName" }
    $owned = $false
    $startUtc = [DateTime]::UtcNow
    $startExit = $null
    $stopExit = $null
    try {
        # Actual-game preflight produced zero events with keyword 0x2/level 0;
        # the same narrow provider with all keywords/level 5 produced Starts
        # and Stops. Do not equate manifest visibility with capture readiness.
        $startExit = Invoke-RecordedTool 'logman.exe' @('create','trace',$SessionName,'-p',"{$providerGuid}",'0xffffffffffffffff','5','-o',$trace,'-ct','perf','-bs','64','-nb','16','64','-ets') (Join-Path $directory 'session-start')
        if ($startExit -ne 0) { throw "ETW start failed ($startExit); see session-start output. No alternate privilege route is attempted." }
        $owned = $true
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 200 }
    }
    finally {
        if ($owned) {
            $stopExit = Invoke-RecordedTool 'logman.exe' @('stop',$SessionName,'-ets') (Join-Path $directory 'session-stop')
        }
        [ordered]@{
            schemaVersion = 1; mode = 'Capture'; provider = $providerGuid; keyword = '0xffffffffffffffff'; level = 5
            sessionName = $SessionName; requestedSeconds = $Seconds; target = $targetEvidence
            startedUtc = $startUtc.ToString('o'); endedUtc = [DateTime]::UtcNow.ToString('o')
            performanceFrequency = [Diagnostics.Stopwatch]::Frequency.ToString($culture)
            clockType = 'QPC'; startExitCode = $startExit; stopExitCode = $stopExit; etlPath = $trace
            note = 'All provider processes are captured; Analyze filters the requested PID. No game or Hook state is changed.'
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $directory 'capture.json') -Encoding utf8
    }
    if ($stopExit -ne 0) { throw "Owned ETW session stop failed ($stopExit); inspect the recorded output and session $SessionName." }
    Write-Output $trace
    return
}

if (-not $EtlPath) { throw 'Analyze requires EtlPath.' }
$trace = (Resolve-Path -LiteralPath $EtlPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $trace) ('analysis-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')) }
$directory = New-EvidenceDirectory $OutputDirectory
$xmlPath = Join-Path $directory 'events-raw.xml'
$summaryPath = Join-Path $directory 'tracerpt-summary.txt'
$decodeExit = Invoke-RecordedTool 'tracerpt.exe' @($trace,'-o',$xmlPath,'-of','XML','-rts') (Join-Path $directory 'tracerpt')
if ($decodeExit -ne 0 -or -not (Test-Path -LiteralPath $xmlPath)) { throw "Offline ETW decode failed ($decodeExit); see tracerpt output." }
# tracerpt forbids combining -rts with -summary. Decode metadata separately;
# Keep the redundant locale-formatted dump as evidence; all timing uses RawTime.
$summaryExit = Invoke-RecordedTool 'tracerpt.exe' @($trace,'-summary',$summaryPath,'-o',(Join-Path $directory 'events-locale.xml'),'-of','XML') (Join-Path $directory 'tracerpt-summary')
[xml]$document = [IO.File]::ReadAllText($xmlPath)
$frequencySource = 'explicit parameter'
$clockHeader = Get-Field $document 'ReservedFlags'
if (-not $PerformanceFrequency) {
    $frequencyText = Get-Field $document 'PerfFreq'
    if ($frequencyText -and $frequencyText.Trim() -match '^\d+$') { $PerformanceFrequency = [UInt64]$frequencyText.Trim(); $frequencySource = 'trace PerfFreq' }
}
if (-not $PerformanceFrequency) {
    $manifestPath = Join-Path (Split-Path -Parent $trace) 'capture.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($manifest.etlPath -eq $trace -and $manifest.clockType -eq 'QPC') {
            $PerformanceFrequency = [UInt64]$manifest.performanceFrequency
            $frequencySource = 'matching capture manifest'
        }
    }
}
$rawEvents = [Collections.Generic.List[object]]::new()
$frames = [Collections.Generic.List[object]]::new()
$pending = @{}
$unmatchedStops = 0
$eventIndex = 0
foreach ($event in $document.SelectNodes('//*[local-name()="Event"]')) {
    $system = $event.SelectSingleNode('./*[local-name()="System"]')
    if (-not $system) { continue }
    $provider = $system.SelectSingleNode('./*[local-name()="Provider"]')
    if (-not $provider -or $provider.GetAttribute('Guid').Trim('{}') -ine $providerGuid) { continue }
    $execution = $system.SelectSingleNode('./*[local-name()="Execution"]')
    $processId = [int]$execution.GetAttribute('ProcessID')
    if ($GameProcessId -gt 0 -and $processId -ne $GameProcessId) { continue }
    $threadId = [int]$execution.GetAttribute('ThreadID')
    $id = [int](Get-Field $system 'EventID')
    $timeNode = $system.SelectSingleNode('./*[local-name()="TimeCreated"]')
    $rawTime = $timeNode.GetAttribute('RawTime')
    if (-not $rawTime) { $rawTime = $timeNode.GetAttribute('SystemTime') }
    $clockKind = 'unknown'
    $timeValue = $null
    $frequency = $PerformanceFrequency
    if ($rawTime -match '^\d+$') { $timeValue = [decimal]$rawTime; $clockKind = 'raw-qpc' }
    else {
        $utc = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse($rawTime,$culture,[Globalization.DateTimeStyles]::RoundtripKind,[ref]$utc)) {
            $timeValue = [decimal]$utc.UtcTicks; $frequency = 10000000; $clockKind = 'decoded-utc-100ns'
        }
    }
    $swapchain = Get-Field $event 'pSwapchain'
    $flags = Get-Field $event 'Flags'
    $result = Get-Field $event 'Result'
    $rawEvents.Add([pscustomobject]@{ eventIndex = $eventIndex; eventId = $id; processId = $processId; threadId = $threadId; swapchain = $swapchain; timestampRaw = $rawTime; timestampKind = $clockKind; flags = $flags; result = $result })
    $eventIndex++
    $threadKey = "$processId|$threadId"
    if ($id -eq 1) {
        $frame = [pscustomobject]@{
            processId = $processId; threadId = $threadId; swapchain = $swapchain
            timestampRaw = $rawTime; timestampKind = $clockKind; frequency = $frequency; timeValue = $timeValue
            flags = $flags; stopTimestampRaw = $null; result = $null; originalPresentMs = $null; intervalMs = $null
        }
        $frames.Add($frame)
        if (-not $pending.ContainsKey($threadKey)) { $pending[$threadKey] = [Collections.Generic.Stack[object]]::new() }
        $pending[$threadKey].Push($frame)
    }
    elseif ($id -eq 2) {
        if ($pending.ContainsKey($threadKey) -and $pending[$threadKey].Count -gt 0) {
            $frame = $pending[$threadKey].Pop()
            $frame.stopTimestampRaw = $rawTime
            $frame.result = $result
            if ($null -ne $timeValue -and $null -ne $frame.timeValue -and $frame.frequency -gt 0 -and $frame.timestampKind -eq $clockKind) {
                $frame.originalPresentMs = [double](($timeValue - $frame.timeValue) * 1000 / $frame.frequency)
            }
        } else { $unmatchedStops++ }
    }
}
$rawEvents | Export-Csv -LiteralPath (Join-Path $directory 'd3d9-events.csv') -NoTypeInformation -Encoding utf8
$streams = [Collections.Generic.List[object]]::new()
foreach ($group in ($frames | Group-Object processId,swapchain,threadId)) {
    $ordered = @($group.Group | Sort-Object timeValue)
    $intervals = [Collections.Generic.List[double]]::new()
    for ($index = 1; $index -lt $ordered.Count; $index++) {
        $before = $ordered[$index - 1]; $after = $ordered[$index]
        if ($null -ne $before.timeValue -and $null -ne $after.timeValue -and $after.frequency -gt 0 -and $before.timestampKind -eq $after.timestampKind) {
            $after.intervalMs = [double](($after.timeValue - $before.timeValue) * 1000 / $after.frequency)
            $intervals.Add($after.intervalMs)
        }
    }
    $badIntervals = @($intervals | Where-Object { $_ -le 0 }).Count
    $badDuration = @($ordered | Where-Object { $null -ne $_.originalPresentMs -and $_.originalPresentMs -lt 0 }).Count
    $streams.Add([pscustomobject]@{
        processId = $ordered[0].processId; swapchain = $ordered[0].swapchain; threadId = $ordered[0].threadId
        presentStarts = $ordered.Count; pairedStops = @($ordered | Where-Object { $null -ne $_.stopTimestampRaw }).Count
        unpairedStarts = @($ordered | Where-Object { $null -eq $_.stopTimestampRaw }).Count
        nonPositiveIntervals = $badIntervals; negativeDurations = $badDuration
        cadenceStatus = $(if ($ordered[0].swapchain -and $ordered.Count -gt 1 -and $intervals.Count -eq $ordered.Count - 1 -and $badIntervals -eq 0) { 'PASS' } else { 'UNVERIFIED' })
        cadence = $(if ($badIntervals -eq 0) { Get-Metrics $intervals.ToArray() } else { $null })
        originalPresentDuration = $(if ($badDuration -eq 0) { Get-Metrics @($ordered | Where-Object { $null -ne $_.originalPresentMs } | ForEach-Object { $_.originalPresentMs }) $false } else { $null })
        resultCounts = @($ordered | Group-Object result | ForEach-Object { [pscustomobject]@{ result = $_.Name; count = $_.Count } })
    })
}
$frames | Select-Object processId,threadId,swapchain,timestampRaw,timestampKind,frequency,flags,stopTimestampRaw,result,originalPresentMs,intervalMs | Export-Csv -LiteralPath (Join-Path $directory 'd3d9-presents.csv') -NoTypeInformation -Encoding utf8
$lossEvidence = [Collections.Generic.List[object]]::new()
# Header counters are explicit trace-wide metadata, not event-table counts.
foreach ($name in @('EventsLost','BuffersLost')) {
    $value = Get-Field $document $name
    if ($null -ne $value -and $value.Trim() -match '^\d+$') {
        $lossEvidence.Add([pscustomobject]@{ field = $name; value = [UInt64]$value.Trim(); source = 'trace header' })
    }
}
if (Test-Path -LiteralPath $summaryPath) {
    $summaryText = [IO.File]::ReadAllText($summaryPath)
    # tracerpt writes a text summary even if the filename ends with .xml.
    if ($summaryText -match '(?im)^\s*Total\s+Events\s+Lost\s+(\d+)\s*$') {
        $lossEvidence.Add([pscustomobject]@{ field = 'EventsLost'; value = [UInt64]$Matches[1]; source = 'tracerpt text summary' })
    }
    $lossXml = $null
    try { $lossXml = [xml]$summaryText } catch { }
    if ($lossXml) { foreach ($node in $lossXml.SelectNodes('//*')) {
        $field = $node.LocalName
        if ($field -ieq 'Data') { $field = $node.GetAttribute('Name') }
        if ($field -imatch '^(EventsLost|BuffersLost|EventsLostCount|BuffersLostCount)$' -and $node.InnerText.Trim() -match '^\d+$') {
            $lossEvidence.Add([pscustomobject]@{ field = $field; value = [UInt64]$node.InnerText.Trim(); source = 'tracerpt summary' })
        }
        foreach ($attribute in $node.Attributes) {
            if ($attribute.LocalName -imatch '^(EventsLost|BuffersLost|EventsLostCount|BuffersLostCount)$' -and $attribute.Value -match '^\d+$') {
                $lossEvidence.Add([pscustomobject]@{ field = $attribute.LocalName; value = [UInt64]$attribute.Value; source = 'tracerpt summary attribute' })
            }
        }
    } }
}
$lossStatus = 'UNVERIFIED'
if (@($lossEvidence | Where-Object { $_.value -gt 0 }).Count -gt 0) { $lossStatus = 'FAIL' }
elseif ($summaryExit -eq 0 -and @($lossEvidence | Where-Object { $_.field -imatch '^EventsLost(Count)?$' }).Count -gt 0 -and
    @($lossEvidence | Where-Object { $_.field -imatch '^BuffersLost(Count)?$' }).Count -gt 0 -and
    @($lossEvidence | Where-Object { $_.source -like 'tracerpt*' }).Count -gt 0) { $lossStatus = 'PASS' }
$swapchains = @($frames | Group-Object processId,swapchain | ForEach-Object {
    $threads = @($_.Group.threadId | Sort-Object -Unique)
    [pscustomobject]@{ processId = $_.Group[0].processId; swapchain = $_.Group[0].swapchain; threadIds = $threads; poolingStatus = $(if ($threads.Count -eq 1) { 'single thread stream' } else { 'UNVERIFIED: multiple threads; metrics remain separate' }) }
})
$report = [ordered]@{
    schemaVersion = 1; mode = 'Analyze'; etlPath = $trace; etlSha256 = (Get-FileHash -LiteralPath $trace -Algorithm SHA256).Hash
    analyzerSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    provider = $providerGuid; gameProcessIdFilter = $GameProcessId; performanceFrequency = $PerformanceFrequency.ToString($culture); frequencySource = $frequencySource
    traceClockReservedFlags = $clockHeader
    rawTimestampStatus = $(if ($frames.Count -gt 0 -and @($frames | Where-Object { $_.timestampKind -ne 'raw-qpc' -or $_.frequency -le 0 }).Count -eq 0) { 'PASS' } else { 'UNVERIFIED: raw timestamp/frequency unavailable; locale dates must not establish performance PASS' })
    presentStarts = $frames.Count; unmatchedStops = $unmatchedStops; lossStatus = $lossStatus; lossEvidence = $lossEvidence.ToArray()
    decodeExitCode = $decodeExit; summaryExitCode = $summaryExit
    measurementStatus = $(if ($frames.Count -lt 2 -or $lossStatus -ne 'PASS' -or @($frames | Where-Object { $_.timestampKind -ne 'raw-qpc' -or $_.frequency -le 0 }).Count -gt 0 -or @($streams | Where-Object { $_.cadenceStatus -ne 'PASS' }).Count -gt 0) { 'UNVERIFIED' } else { 'MEASURED: inspect per-stream validity; no performance acceptance implied' })
    definitions = [ordered]@{
        averageFps = '1000 / mean(all consecutive PresentStart intervals in each PID/swapchain/thread stream)'
        onePercentLowFps = '1000 / mean(largest ceil(N * 0.01) intervals)'
        percentiles = 'Nearest rank: sorted[ceil(p * N) - 1]'
        gaps = 'No long gaps, warmup, failed presents, or outliers are silently removed; Result and raw events are retained.'
        originalPresentDuration = 'Runtime PresentStart to matching PresentStop on the same PID/TID stack; excludes Hook before runtime entry; not total Hook overhead.'
        scope = 'Game runtime CPU Present cadence, not displayed FPS or OSC compositor FPS. Multiple threads/swapchains are not pooled.'
    }
    swapchains = $swapchains; streams = $streams.ToArray()
}
$report | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $directory 'summary.json') -Encoding utf8
Write-Output (Join-Path $directory 'summary.json')
