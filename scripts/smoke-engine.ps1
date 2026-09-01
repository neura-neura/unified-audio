param(
    [string]$InputName = "",
    [string]$OutputName = "",
    [ValidateSet("Voice", "System", "Both")]
    [string]$MixerMode = "Voice",
    [string]$SystemDeviceName = "",
    [string]$PluginQuery = "",
    [int]$BufferSize = 0,
    [int]$RunSeconds = 0,
    [int]$WaitForScanSeconds = 0,
    [switch]$TestProcessFilter,
    [switch]$TestExclusionFilter,
    [switch]$TestAtomicRollback,
    [switch]$TestProfilePluginPreset,
    [string]$EnginePath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($EnginePath)) {
    $EnginePath = Join-Path $repositoryRoot "src\UnifiedAudio.EngineHost\build\UnifiedAudioEngineHost_artefacts\Release\UnifiedAudio Engine Host.exe"
}
$EnginePath = (Resolve-Path -LiteralPath $EnginePath).Path

$engineProcess = Start-Process -FilePath $EnginePath -WindowStyle Hidden -PassThru
$pipe = $null
$sequence = 0L

function Send-EngineCommand {
    param([string]$Name, [object]$Payload = @{})

    $script:sequence++
    $request = [ordered]@{
        contractVersion = 1
        sequence = $script:sequence
        kind = "command"
        name = $Name
        messageId = [guid]::NewGuid().ToString()
        payload = $Payload
    } | ConvertTo-Json -Compress -Depth 12
    $body = [Text.Encoding]::UTF8.GetBytes($request)
    if ($body.Length -gt 8MB) { throw "Engine request exceeds the contract limit." }
    $header = [BitConverter]::GetBytes([int]$body.Length)
    $script:pipe.Write($header, 0, $header.Length)
    $script:pipe.Write($body, 0, $body.Length)
    $script:pipe.Flush()

    $replyHeader = [byte[]]::new(4)
    $script:pipe.ReadExactly($replyHeader, 0, 4)
    $replyLength = [BitConverter]::ToInt32($replyHeader, 0)
    if ($replyLength -le 0 -or $replyLength -gt 8MB) { throw "Engine response has an invalid length." }
    $replyBody = [byte[]]::new($replyLength)
    $script:pipe.ReadExactly($replyBody, 0, $replyLength)
    $response = [Text.Encoding]::UTF8.GetString($replyBody) | ConvertFrom-Json
    if ($null -ne $response.error) { throw "$($response.error.code): $($response.error.message)" }
    return $response.payload
}

try {
    $pipe = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        "UnifiedAudio.Engine.v1",
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::None)
    $pipe.Connect(12000)

    $hello = Send-EngineCommand "hello"
    $scanWaitCompleted = $null
    if ($WaitForScanSeconds -gt 0) {
        $scanDeadline = [DateTime]::UtcNow.AddSeconds($WaitForScanSeconds)
        do {
            $scanSnapshot = Send-EngineCommand "engine.snapshot"
            if (-not $scanSnapshot.scanning) {
                $scanWaitCompleted = $true
                break
            }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $scanDeadline)
        if ($scanWaitCompleted -ne $true) { throw "Plugin fingerprint/scan did not finish within $WaitForScanSeconds seconds." }
    }
    $devices = Send-EngineCommand "devices.list"
    $sessions = Send-EngineCommand "audio.sessions.list" @{ deviceName = $SystemDeviceName }
    $processFilterRoundTrip = $null
    $processFilterTarget = $sessions.sessions | Where-Object { $_.active } | Select-Object -First 1
    if (($TestProcessFilter -or $TestExclusionFilter) -and $null -ne $processFilterTarget) {
        $filterOn = Send-EngineCommand "mixer.processFilter.configure" @{
            enabled = $true
            exclusionMode = [bool]$TestExclusionFilter
            systemDeviceName = ""
            rules = @(@{
                key = $processFilterTarget.key
                displayName = $processFilterTarget.displayName
                gain = 0.75
                excluded = [bool]$TestExclusionFilter
            })
        }
        $processFilterRoundTrip = $filterOn.processFilterEnabled -and $filterOn.processRules.Count -eq 1 `
            -and $filterOn.processFilterExclusionMode -eq [bool]$TestExclusionFilter `
            -and $filterOn.processRules[0].excluded -eq [bool]$TestExclusionFilter
        if ([string]::IsNullOrWhiteSpace($InputName) -or [string]::IsNullOrWhiteSpace($OutputName)) {
            $filterOff = Send-EngineCommand "mixer.processFilter.configure" @{
                enabled = $false
                systemDeviceName = $SystemDeviceName
                rules = @()
            }
            $processFilterRoundTrip = $processFilterRoundTrip -and -not $filterOff.processFilterEnabled
        }
    }
    $pluginsBefore = Send-EngineCommand "plugins.list"
    Send-EngineCommand "plugin.add" @{ path = "builtin:mono2stereo" } | Out-Null
    $pluginsAfter = Send-EngineCommand "plugins.list"
    $addedIndex = $pluginsAfter.chain.Count - 1
    Send-EngineCommand "plugin.bypass" @{ index = $addedIndex; bypassed = $true } | Out-Null
    Send-EngineCommand "plugin.remove" @{ index = $addedIndex } | Out-Null
    $profilePluginRoundTrip = $null
    $profilePluginRollback = $null
    if ($TestProfilePluginPreset) {
        $profilePluginsBefore = Send-EngineCommand "profile.engine.capture"
        $withAdapter = @($profilePluginsBefore.plugins) + @(@{
            id = "builtin:mono2stereo"
            bypassed = $false
            stateBase64 = ""
        })
        Send-EngineCommand "pipeline.configure" @{
            configureEngine = $false
            configureMixer = $false
            configurePlugins = $true
            plugins = $withAdapter
            applyMute = $false
        } | Out-Null
        $capturedWithAdapter = Send-EngineCommand "profile.engine.capture"
        Send-EngineCommand "pipeline.configure" @{
            configureEngine = $false
            configureMixer = $false
            configurePlugins = $true
            plugins = @($profilePluginsBefore.plugins)
            applyMute = $false
        } | Out-Null
        $profilePluginsRestored = Send-EngineCommand "profile.engine.capture"
        $profilePluginRoundTrip = $capturedWithAdapter.plugins.Count -eq $profilePluginsBefore.plugins.Count + 1 `
            -and $capturedWithAdapter.plugins[-1].id -eq "builtin:mono2stereo" `
            -and $profilePluginsRestored.plugins.Count -eq $profilePluginsBefore.plugins.Count `
            -and (@($profilePluginsRestored.plugins.id) -join "|") -eq (@($profilePluginsBefore.plugins.id) -join "|")

        try {
            Send-EngineCommand "pipeline.configure" @{
                configureEngine = $false
                configureMixer = $false
                configurePlugins = $true
                plugins = @(@{ id = "__missing_plugin__"; bypassed = $false; stateBase64 = "" })
                applyMute = $false
            } | Out-Null
            throw "The missing-plugin transaction unexpectedly succeeded."
        }
        catch {
            if ($_.Exception.Message -notlike "pipeline-configure:*") { throw }
        }
        $afterPluginFailure = Send-EngineCommand "profile.engine.capture"
        $profilePluginRollback = $afterPluginFailure.plugins.Count -eq $profilePluginsBefore.plugins.Count `
            -and (@($afterPluginFailure.plugins.id) -join "|") -eq (@($profilePluginsBefore.plugins.id) -join "|")
    }

    $pluginUnderTest = $null
    $pluginUnderTestIndex = -1
    if (-not [string]::IsNullOrWhiteSpace($PluginQuery)) {
        $pluginUnderTest = $pluginsBefore.catalog | Where-Object {
            $_.name -like "*$PluginQuery*" -or $_.id -like "*$PluginQuery*"
        } | Select-Object -First 1
        if ($null -eq $pluginUnderTest) { throw "Plugin not found in catalog: $PluginQuery" }
        Send-EngineCommand "plugin.add" @{ path = $pluginUnderTest.id } | Out-Null
        $withTestPlugin = Send-EngineCommand "plugins.list"
        $pluginUnderTestIndex = $withTestPlugin.chain.Count - 1
    }

    $started = $null
    $settled = $null
    $atomicRollback = $null
    $atomicRollbackError = $null
    if (-not [string]::IsNullOrWhiteSpace($InputName) -and -not [string]::IsNullOrWhiteSpace($OutputName)) {
        $inputEndpointId = ($devices.inputEndpoints | Where-Object { $_.name -eq $InputName } | Select-Object -First 1).id
        $outputEndpointId = ($devices.outputEndpoints | Where-Object { $_.name -eq $OutputName } | Select-Object -First 1).id
        $pipelineRules = @()
        if (($TestProcessFilter -or $TestExclusionFilter) -and $null -ne $processFilterTarget) {
            $pipelineRules = @(@{
                key = $processFilterTarget.key
                displayName = $processFilterTarget.displayName
                gain = 0.75
                excluded = [bool]$TestExclusionFilter
            })
        }
        $pipelinePayload = @{
            configureEngine = $true
            inputName = $InputName
            outputName = $OutputName
            bufferSize = $BufferSize
            inputId = $inputEndpointId
            outputId = $outputEndpointId
            configureMixer = $true
            mode = $MixerMode
            systemDeviceName = $SystemDeviceName
            voiceGain = 1.0
            systemGain = 1.0
            duckingEnabled = $true
            duckAmount = 0.55
            voiceThresholdDb = -36.0
            attackMs = 20
            holdMs = 200
            releaseMs = 280
            processFilterEnabled = [bool]($TestProcessFilter -or $TestExclusionFilter)
            processFilterExclusionMode = [bool]$TestExclusionFilter
            rules = $pipelineRules
            applyMute = $true
            muted = $true
        }
        $started = Send-EngineCommand "pipeline.configure" $pipelinePayload
        if ($TestAtomicRollback) {
            $beforeFailure = Send-EngineCommand "engine.snapshot"
            $invalidPayload = $pipelinePayload.Clone()
            $invalidPayload.outputName = "__UnifiedAudio missing endpoint__"
            $invalidPayload.outputId = ""
            try {
                Send-EngineCommand "pipeline.configure" $invalidPayload | Out-Null
                throw "The invalid pipeline transaction unexpectedly succeeded."
            }
            catch {
                $atomicRollbackError = $_.Exception.Message
                if ($atomicRollbackError -notlike "pipeline-configure:*") { throw }
            }
            $afterFailure = Send-EngineCommand "engine.snapshot"
            $atomicRollback = $afterFailure.running -eq $beforeFailure.running `
                -and $afterFailure.inputDeviceId -eq $beforeFailure.inputDeviceId `
                -and $afterFailure.outputDeviceId -eq $beforeFailure.outputDeviceId `
                -and $afterFailure.bufferSize -eq $beforeFailure.bufferSize `
                -and $afterFailure.mixerMode -eq $beforeFailure.mixerMode `
                -and $afterFailure.processFilterEnabled -eq $beforeFailure.processFilterEnabled `
                -and $afterFailure.processFilterExclusionMode -eq $beforeFailure.processFilterExclusionMode `
                -and $afterFailure.processRules.Count -eq $beforeFailure.processRules.Count `
                -and $afterFailure.mutedRequested -eq $beforeFailure.mutedRequested
        }
        Start-Sleep -Milliseconds 250
        $baseline = Send-EngineCommand "engine.snapshot"
        if ($RunSeconds -gt 0) { Start-Sleep -Seconds $RunSeconds }
        $settled = Send-EngineCommand "engine.snapshot"
        Send-EngineCommand "engine.stop" | Out-Null
        if (($TestProcessFilter -or $TestExclusionFilter) -and $null -ne $processFilterTarget) {
            $filterOff = Send-EngineCommand "mixer.processFilter.configure" @{
                enabled = $false
                systemDeviceName = $SystemDeviceName
                rules = @()
            }
            $processFilterRoundTrip = $processFilterRoundTrip -and -not $filterOff.processFilterEnabled
        }
    }

    if ($pluginUnderTestIndex -ge 0) {
        Send-EngineCommand "plugin.remove" @{ index = $pluginUnderTestIndex } | Out-Null
    }

    [pscustomobject]@{
        ContractVersion = $hello.contractVersion
        EngineVersion = $hello.engineVersion
        ScanWaitCompleted = $scanWaitCompleted
        Inputs = $devices.inputs.Count
        Outputs = $devices.outputs.Count
        AudioSessions = $sessions.sessions.Count
        ProcessFilterTarget = $processFilterTarget.displayName
        ProcessFilterRoundTrip = $processFilterRoundTrip
        ProcessRulesInSnapshot = if ($null -ne $filterOn) { $filterOn.processRules.Count } else { 0 }
        CatalogPlugins = $pluginsBefore.catalog.Count
        PluginMutationRoundTrip = ($pluginsAfter.chain.Count -eq $pluginsBefore.chain.Count + 1)
        ProfilePluginRoundTrip = $profilePluginRoundTrip
        ProfilePluginRollback = $profilePluginRollback
        PluginUnderTest = $pluginUnderTest.name
        RunningAfterStart = $started.running
        CaptureSampleRate = $started.captureSampleRate
        GraphSampleRate = $started.graphSampleRate
        StableInputIdRoundTrip = if ($null -ne $started) { $started.inputDeviceId -eq $inputEndpointId } else { $null }
        StableOutputIdRoundTrip = if ($null -ne $started) { $started.outputDeviceId -eq $outputEndpointId } else { $null }
        AtomicRollback = $atomicRollback
        AtomicRollbackError = $atomicRollbackError
        MuteSettled = $settled.muteSettled
        InputPeak = $settled.inputPeak
        OutputPeak = $settled.outputPeak
        Underruns = $settled.underruns
        UnderrunsDuringRun = if ($null -ne $baseline) { $settled.underruns - $baseline.underruns } else { $null }
        Overruns = $settled.overruns
        QueuedCaptureFrames = $settled.queuedCaptureFrames
        MixerMode = $settled.mixerMode
        SystemCaptureRunning = $settled.systemCaptureRunning
        SystemPeak = $settled.systemPeak
        SpectrumBands = if ($null -ne $settled) { $settled.spectrum.Count } else { $null }
        SpectrumMaximum = if ($null -ne $settled) { ($settled.spectrum | Measure-Object -Maximum).Maximum } else { $null }
    }
}
finally {
    if ($null -ne $pipe) {
        try { Send-EngineCommand "host.shutdown" | Out-Null } catch { }
        $pipe.Dispose()
    }
    if (-not $engineProcess.WaitForExit(5000)) {
        Stop-Process -Id $engineProcess.Id -Force
        throw "Engine host did not exit after host.shutdown."
    }
    $engineProcess.Dispose()
}
