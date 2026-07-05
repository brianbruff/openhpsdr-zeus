param(
    [string]$BaseUrl = "http://127.0.0.1:6061",
    [int]$DurationSec = 900,
    [int]$IntervalMs = 125,
    [switch]$ArmFreeDv,
    [string]$Submode = "RadeV1",
    [int]$LowHz = 300,
    [int]$HighHz = 2700,
    [double]$AudibleRmsThresholdDbfs = -72.0,
    [string]$OutputDir = "captures\dsp-live-diagnostics\freedv-tail-monitor"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Net.Http

function Join-Url([string]$base, [string]$path) {
    return $base.TrimEnd("/") + "/" + $path.TrimStart("/")
}

function Read-Json([System.Net.Http.HttpClient]$client, [string]$url) {
    $text = $client.GetStringAsync($url).GetAwaiter().GetResult()
    return $text | ConvertFrom-Json
}

function Post-Json([System.Net.Http.HttpClient]$client, [string]$url, [object]$body, [string]$method = "POST") {
    $json = $body | ConvertTo-Json -Compress -Depth 8
    $content = [System.Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, "application/json")
    $request = [System.Net.Http.HttpRequestMessage]::new([System.Net.Http.HttpMethod]::new($method), $url)
    $request.Content = $content
    $response = $client.SendAsync($request).GetAwaiter().GetResult()
    $response.EnsureSuccessStatusCode() | Out-Null
    if ($response.Content.Headers.ContentLength -eq 0) { return $null }
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return $text | ConvertFrom-Json
}

function Number-OrNull($value) {
    if ($null -eq $value) { return $null }
    try {
        $n = [double]$value
        if ([double]::IsNaN($n) -or [double]::IsInfinity($n)) { return $null }
        return $n
    } catch {
        return $null
    }
}

function Get-Prop($object, [string[]]$path) {
    $current = $object
    foreach ($part in $path) {
        if ($null -eq $current) { return $null }
        $prop = $current.PSObject.Properties[$part]
        if ($null -eq $prop) { return $null }
        $current = $prop.Value
    }
    return $current
}

function Summarize-Tail($samples, [int]$startIndex, [int]$endIndex) {
    if ($startIndex -lt 0 -or $endIndex -lt $startIndex) { return $null }
    $slice = $samples[$startIndex..$endIndex]
    $audible = @($slice | Where-Object {
        $_.mode -eq "FreeDv" -and $_.freedvActive -and -not $_.freedvSynced -and $_.audible
    })
    $maxRms = ($audible | Where-Object { $null -ne $_.audioRmsDbfs } | Measure-Object -Property audioRmsDbfs -Maximum).Maximum
    $maxPeak = ($audible | Where-Object { $null -ne $_.audioPeakDbfs } | Measure-Object -Property audioPeakDbfs -Maximum).Maximum
    return [pscustomobject]@{
        sampleStart = $startIndex
        sampleEnd = $endIndex
        totalSamples = $slice.Count
        audibleSamples = $audible.Count
        audibleMs = [int]($audible.Count * $IntervalMs)
        maxRmsDbfs = $maxRms
        maxPeakDbfs = $maxPeak
    }
}

$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromMilliseconds([Math]::Max(750, $IntervalMs * 5))

if ($ArmFreeDv) {
    Post-Json $client (Join-Url $BaseUrl "/api/plugins/org.openhpsdr.freedv/config") @{
        submode = $Submode
        autoDetect = $false
        squelchEnabled = $true
        snrSquelchThreshDb = 0
    } "PUT" | Out-Null
    Post-Json $client (Join-Url $BaseUrl "/api/mode") @{ mode = 10; receiver = 0 } | Out-Null
    Post-Json $client (Join-Url $BaseUrl "/api/filter") @{
        lowHz = $LowHz
        highHz = $HighHz
        receiver = 0
        presetName = "VAR1"
    } | Out-Null
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$stamp = Get-Date -Format "yyyyMMddTHHmmss"
$outFile = Join-Path $OutputDir "freedv-realtime-$stamp.json"

$sidecarUrl = $null
try {
    $p3 = Read-Json $client (Join-Url $BaseUrl "/api/protocol3/sidecar")
    $sidecarUrl = $p3.bridge.diagnosticsUrl
} catch {
    Write-Warning "Could not read Protocol 3 sidecar URL: $($_.Exception.Message)"
}

$samples = New-Object System.Collections.Generic.List[object]
$events = New-Object System.Collections.Generic.List[object]
$tailSummaries = New-Object System.Collections.Generic.List[object]
$sw = [Diagnostics.Stopwatch]::StartNew()
$lastPrint = [DateTime]::MinValue
$lastSynced = $null
$lastMode = $null
$lastActive = $null
$lastAudioFrames = $null
$lastP3Frames = $null
$lastAt = $null
$syncOffSampleIndex = -1

Write-Host "FreeDV realtime monitor"
Write-Host "BaseUrl=$BaseUrl DurationSec=$DurationSec IntervalMs=$IntervalMs Output=$outFile"
Write-Host "Tail condition: mode=FreeDv active=true synced=false audible RMS > $AudibleRmsThresholdDbfs dBFS"

while ($sw.Elapsed.TotalSeconds -lt $DurationSec) {
    $pollStarted = Get-Date
    try {
        $state = Read-Json $client (Join-Url $BaseUrl "/api/state")
        $freedv = Read-Json $client (Join-Url $BaseUrl "/api/plugins/org.openhpsdr.freedv/status")
        $live = Read-Json $client (Join-Url $BaseUrl "/api/dsp/live-diagnostics")
        $p3 = Read-Json $client (Join-Url $BaseUrl "/api/protocol3/sidecar")
        $native = Read-Json $client (Join-Url $BaseUrl "/api/audio/native")
        $side = $null
        if ($sidecarUrl) {
            try { $side = Read-Json $client $sidecarUrl } catch { $side = $null }
        }

        $evidence = $live.runtimeEvidence
        $audioRms = Number-OrNull $evidence.audioRmsDbfs
        $audioPeak = Number-OrNull $evidence.audioPeakDbfs
        $audible = $evidence.audioStatus -eq "fresh" -and $null -ne $audioRms -and $audioRms -gt $AudibleRmsThresholdDbfs

        $sideQueue = Get-Prop $side @("hostedRadioSession", "audioQueuedFrames")
        $sideDrops = Get-Prop $side @("hostedRadioSession", "audioFramesDroppedTotal")
        $sideRateRatio = Get-Prop $side @("hostedRadioSession", "streamHealth", "audioRateRatio")
        $sideStatus = Get-Prop $side @("hostedRadioSession", "streamHealth", "status")
        $sidePassbandLow = Get-Prop $side @("p3", "streams") | Select-Object -First 1 | ForEach-Object { $_.passbandLowHz }
        $sidePassbandHigh = Get-Prop $side @("p3", "streams") | Select-Object -First 1 | ForEach-Object { $_.passbandHighHz }
        $nativeDiag = $native.diagnostics

        $row = [pscustomobject]@{
            ts = $pollStarted.ToString("o")
            elapsedMs = [int]$sw.Elapsed.TotalMilliseconds
            mode = [string]$state.mode
            vfoHz = [int64]$state.vfoHz
            radioLoHz = [int64]$state.radioLoHz
            ctunEnabled = [bool]$state.ctunEnabled
            filterLowHz = [int]$state.filterLowHz
            filterHighHz = [int]$state.filterHighHz
            freedvActive = [bool]$freedv.active
            freedvSynced = [bool]$freedv.synced
            freedvSnrDb = Number-OrNull $freedv.snrDb
            freedvSubmode = [string]$freedv.submode
            freedvAutoDetect = [bool]$freedv.autoDetect
            freedvSquelchEnabled = [bool]$freedv.squelchEnabled
            audioStatus = [string]$evidence.audioStatus
            audioSource = [string]$evidence.audioSource
            audioAgeMs = Number-OrNull $evidence.audioAgeMs
            audioFramesBroadcast = Number-OrNull $evidence.audioFramesBroadcast
            audioLastSeq = Number-OrNull $evidence.audioLastSeq
            audioSampleCount = Number-OrNull $evidence.audioSampleCount
            audioRmsDbfs = $audioRms
            audioPeakDbfs = $audioPeak
            audible = [bool]$audible
            p3FramesForwarded = Number-OrNull $p3.frames.audio.framesForwarded
            p3LastFrameAgeMs = Number-OrNull $p3.frames.audio.lastFrameAgeMs
            p3LastSampleCount = Number-OrNull $p3.frames.audio.lastSampleCount
            p3PollFailures = Number-OrNull $p3.frames.audio.pollFailures
            p3TxIqStreamingArmed = [bool]$p3.bridge.txIqStreamingArmed
            p3ControlActive = [bool]$p3.control.active
            p3ControlTune = [bool]$p3.control.tune
            p3ControlTxMoxOn = [bool]$p3.control.txMoxOn
            p3ControlTxTunOn = [bool]$p3.control.txTunOn
            p3ControlLatchedMox = [bool]$p3.control.latchedMox
            p3ControlLatchedTune = [bool]$p3.control.latchedTune
            p3SidecarQueueDepth = Number-OrNull $sideQueue
            p3SidecarDroppedFrames = Number-OrNull $sideDrops
            p3SidecarAudioRateRatio = Number-OrNull $sideRateRatio
            p3SidecarStreamStatus = [string]$sideStatus
            p3SidecarPassbandLowHz = Number-OrNull $sidePassbandLow
            p3SidecarPassbandHighHz = Number-OrNull $sidePassbandHigh
            nativeRingDepthSamples = Number-OrNull $nativeDiag.ringDepthSamples
            nativePrebufferSamples = Number-OrNull $nativeDiag.prebufferSamples
            nativeUnderrunSamplesTotal = Number-OrNull $nativeDiag.underrunSamplesTotal
            nativeOverrunSamplesTotal = Number-OrNull $nativeDiag.overrunSamplesTotal
            nativeRebufferEvents = Number-OrNull $nativeDiag.rebufferEvents
        }
        $samples.Add($row) | Out-Null
        $sampleIndex = $samples.Count - 1

        if ($lastSynced -ne $null -and $row.freedvSynced -ne $lastSynced) {
            $kind = if ($row.freedvSynced) { "sync-on" } else { "sync-off" }
            $events.Add([pscustomobject]@{
                ts = $row.ts
                elapsedMs = $row.elapsedMs
                kind = $kind
                mode = $row.mode
                active = $row.freedvActive
                snrDb = $row.freedvSnrDb
                audioStatus = $row.audioStatus
                rmsDbfs = $row.audioRmsDbfs
                peakDbfs = $row.audioPeakDbfs
            }) | Out-Null
            Write-Host ("{0:HH:mm:ss.fff} {1} mode={2} active={3} snr={4} audio={5} rms={6} peak={7}" -f $pollStarted, $kind, $row.mode, $row.freedvActive, $row.freedvSnrDb, $row.audioStatus, $row.audioRmsDbfs, $row.audioPeakDbfs)
            if ($kind -eq "sync-off" -and $row.mode -eq "FreeDv" -and $row.freedvActive) {
                $syncOffSampleIndex = $sampleIndex
            }
        }

        if ($lastMode -ne $null -and $row.mode -ne $lastMode) {
            if ($syncOffSampleIndex -ge 0) {
                $summary = Summarize-Tail $samples $syncOffSampleIndex ([Math]::Max($syncOffSampleIndex, $sampleIndex - 1))
                if ($null -ne $summary) { $tailSummaries.Add($summary) | Out-Null }
                $syncOffSampleIndex = -1
            }
            $events.Add([pscustomobject]@{
                ts = $row.ts
                elapsedMs = $row.elapsedMs
                kind = "mode-change"
                from = $lastMode
                to = $row.mode
                active = $row.freedvActive
                synced = $row.freedvSynced
                audioStatus = $row.audioStatus
                rmsDbfs = $row.audioRmsDbfs
            }) | Out-Null
            Write-Host ("{0:HH:mm:ss.fff} mode-change {1}->{2} active={3} synced={4} audio={5} rms={6}" -f $pollStarted, $lastMode, $row.mode, $row.freedvActive, $row.freedvSynced, $row.audioStatus, $row.audioRmsDbfs)
        }

        if ($lastActive -ne $null -and $row.freedvActive -ne $lastActive) {
            if ($syncOffSampleIndex -ge 0 -and -not $row.freedvActive) {
                $summary = Summarize-Tail $samples $syncOffSampleIndex ([Math]::Max($syncOffSampleIndex, $sampleIndex - 1))
                if ($null -ne $summary) { $tailSummaries.Add($summary) | Out-Null }
                $syncOffSampleIndex = -1
            }
            $events.Add([pscustomobject]@{
                ts = $row.ts
                elapsedMs = $row.elapsedMs
                kind = "active-change"
                mode = $row.mode
                active = $row.freedvActive
                synced = $row.freedvSynced
                audioStatus = $row.audioStatus
                rmsDbfs = $row.audioRmsDbfs
            }) | Out-Null
            Write-Host ("{0:HH:mm:ss.fff} active-change mode={1} active={2} synced={3} audio={4} rms={5}" -f $pollStarted, $row.mode, $row.freedvActive, $row.freedvSynced, $row.audioStatus, $row.audioRmsDbfs)
        }

        if ($syncOffSampleIndex -ge 0 -and $sampleIndex - $syncOffSampleIndex -ge [Math]::Ceiling(3000.0 / $IntervalMs)) {
            $summary = Summarize-Tail $samples $syncOffSampleIndex $sampleIndex
            if ($null -ne $summary) {
                $tailSummaries.Add($summary) | Out-Null
                if ($summary.audibleSamples -gt 0) {
                    Write-Host ("{0:HH:mm:ss.fff} tail-audible audibleMs={1} maxRms={2} maxPeak={3}" -f $pollStarted, $summary.audibleMs, $summary.maxRmsDbfs, $summary.maxPeakDbfs)
                }
            }
            $syncOffSampleIndex = -1
        }

        if ((Get-Date) -gt $lastPrint.AddSeconds(5)) {
            $hostFps = $null
            $p3Fps = $null
            if ($lastAt -ne $null) {
                $dt = ($pollStarted - $lastAt).TotalSeconds
                if ($dt -gt 0 -and $null -ne $lastAudioFrames -and $null -ne $row.audioFramesBroadcast) {
                    $hostFps = [Math]::Round(($row.audioFramesBroadcast - $lastAudioFrames) / $dt, 1)
                }
                if ($dt -gt 0 -and $null -ne $lastP3Frames -and $null -ne $row.p3FramesForwarded) {
                    $p3Fps = [Math]::Round(($row.p3FramesForwarded - $lastP3Frames) / $dt, 1)
                }
            }
            Write-Host ("{0:HH:mm:ss} mode={1} active={2} sync={3} snr={4} audio={5} rms={6} hostFps={7} p3Fps={8} p3Age={9}ms q={10} drops={11} ring={12} tx={13} tun={14} armed={15}" -f $pollStarted, $row.mode, $row.freedvActive, $row.freedvSynced, $row.freedvSnrDb, $row.audioStatus, $row.audioRmsDbfs, $hostFps, $p3Fps, $row.p3LastFrameAgeMs, $row.p3SidecarQueueDepth, $row.p3SidecarDroppedFrames, $row.nativeRingDepthSamples, $row.p3ControlTxMoxOn, $row.p3ControlTxTunOn, $row.p3TxIqStreamingArmed)
            $lastPrint = Get-Date
            $lastAt = $pollStarted
            $lastAudioFrames = $row.audioFramesBroadcast
            $lastP3Frames = $row.p3FramesForwarded
        }

        $lastSynced = $row.freedvSynced
        $lastMode = $row.mode
        $lastActive = $row.freedvActive
    } catch {
        $events.Add([pscustomobject]@{
            ts = (Get-Date).ToString("o")
            elapsedMs = [int]$sw.Elapsed.TotalMilliseconds
            kind = "poll-error"
            error = $_.Exception.Message
        }) | Out-Null
        Write-Host "poll-error $($_.Exception.Message)"
    }

    $spentMs = [int]((Get-Date) - $pollStarted).TotalMilliseconds
    Start-Sleep -Milliseconds ([Math]::Max(10, $IntervalMs - $spentMs))
}

if ($syncOffSampleIndex -ge 0 -and $samples.Count -gt $syncOffSampleIndex) {
    $summary = Summarize-Tail $samples $syncOffSampleIndex ($samples.Count - 1)
    if ($null -ne $summary) { $tailSummaries.Add($summary) | Out-Null }
}

$freeDvUnsyncedAudible = @($samples | Where-Object {
    $_.mode -eq "FreeDv" -and $_.freedvActive -and -not $_.freedvSynced -and $_.audible
})

$summaryObject = [pscustomobject]@{
    schemaVersion = 1
    baseUrl = $BaseUrl
    startedAt = if ($samples.Count -gt 0) { $samples[0].ts } else { (Get-Date).ToString("o") }
    endedAt = (Get-Date).ToString("o")
    durationSec = $DurationSec
    intervalMs = $IntervalMs
    audibleRmsThresholdDbfs = $AudibleRmsThresholdDbfs
    sampleCount = $samples.Count
    eventCount = $events.Count
    syncOnCount = @($events | Where-Object { $_.kind -eq "sync-on" }).Count
    syncOffCount = @($events | Where-Object { $_.kind -eq "sync-off" }).Count
    modeChangeCount = @($events | Where-Object { $_.kind -eq "mode-change" }).Count
    freeDvUnsyncedAudibleSamples = $freeDvUnsyncedAudible.Count
    freeDvUnsyncedAudibleMs = [int]($freeDvUnsyncedAudible.Count * $IntervalMs)
    freeDvUnsyncedAudibleMaxRmsDbfs = ($freeDvUnsyncedAudible | Where-Object { $null -ne $_.audioRmsDbfs } | Measure-Object -Property audioRmsDbfs -Maximum).Maximum
    freeDvUnsyncedAudibleMaxPeakDbfs = ($freeDvUnsyncedAudible | Where-Object { $null -ne $_.audioPeakDbfs } | Measure-Object -Property audioPeakDbfs -Maximum).Maximum
    maxHostAudioAgeMs = ($samples | Where-Object { $null -ne $_.audioAgeMs } | Measure-Object -Property audioAgeMs -Maximum).Maximum
    maxP3AudioAgeMs = ($samples | Where-Object { $null -ne $_.p3LastFrameAgeMs } | Measure-Object -Property p3LastFrameAgeMs -Maximum).Maximum
    final = if ($samples.Count -gt 0) { $samples[$samples.Count - 1] } else { $null }
    tailSummaries = $tailSummaries
    events = $events
    samples = $samples
}

$summaryObject | ConvertTo-Json -Depth 10 | Set-Content -Path $outFile -Encoding UTF8
$summaryObject |
    Select-Object durationSec, sampleCount, eventCount, syncOnCount, syncOffCount, modeChangeCount, freeDvUnsyncedAudibleSamples, freeDvUnsyncedAudibleMs, freeDvUnsyncedAudibleMaxRmsDbfs, maxHostAudioAgeMs, maxP3AudioAgeMs |
    ConvertTo-Json -Depth 4
Write-Host "saved $outFile"
