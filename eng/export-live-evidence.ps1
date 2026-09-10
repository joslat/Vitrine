# SPDX-License-Identifier: MIT
#requires -Version 7.0

<#
.SYNOPSIS
Exports one sanitized public receipt from a VITRINE live-evaluation outcome.

.DESCRIPTION
Reads a schema-1.3 or schema-1.4 outcome.json produced by Eval02, Eval03 or Eval06, validates its shape and
typed vocabulary, and writes a strictly allow-listed JSON receipt plus a self-contained
HTML view. Queries, expected answers, ground truth, subject responses, tool arguments,
judge explanations, provider detail, run paths, endpoints, credentials and canaries are
never copied. Both output files are staged beside their destinations after the complete
export is rendered; if the second replacement fails, rollback restores the prior pair.

.PARAMETER InputPath
Path to the private schema-1.3 or schema-1.4 live-session outcome.json.

.PARAMETER JsonPath
Destination for the public JSON receipt.

.PARAMETER HtmlPath
Destination for the self-contained public HTML receipt.

.EXAMPLE
pwsh ./eng/export-live-evidence.ps1 `
  -InputPath ./.agenteval/live/live-sessions/<session>/outcome.json `
  -JsonPath ./docs/evidence/vitrine-live-eval03.json `
  -HtmlPath ./docs/evidence/vitrine-live-eval03.html

.NOTES
This exporter accepts only liveEval02Workflow, liveEval03AgentVsWorkflow and
liveEval06SafetyProbes. It fails closed
on an unsupported schema, an unknown property, an unknown enum-like value, inconsistent
counts, or unsafe text selected for publication.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$JsonPath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$HtmlPath,

    [Parameter(Mandatory = $false)]
    [string]$ImplementationCommit,

    [Parameter(Mandatory = $false, DontShow = $true)]
    [switch]$TestFailSecondPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Path, [string]$Message) {
    throw "Live-evidence export rejected '$Path': $Message"
}

function Has-Property([object]$Value, [string]$Name) {
    if ($Value -is [System.Collections.IDictionary]) { return $Value.Contains($Name) }
    return $null -ne $Value -and $null -ne $Value.PSObject.Properties[$Name]
}

function Property([object]$Value, [string]$Name) {
    if ($Value -is [System.Collections.IDictionary]) {
        $propertyValue = $Value[$Name]
    } else {
        $propertyValue = $Value.PSObject.Properties[$Name].Value
    }
    if ($propertyValue -isnot [string] -and $propertyValue -is [System.Collections.IEnumerable]) {
        return ,$propertyValue
    }
    return $propertyValue
}

function OptionalProperty([object]$Value, [string]$Name) {
    if (-not (Has-Property $Value $Name)) { return $null }
    return (Property $Value $Name)
}

function Assert-Object([object]$Value, [string]$Path) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [System.Collections.IEnumerable]) {
        Fail $Path 'expected a JSON object.'
    }
}

function Assert-Shape(
    [object]$Value,
    [string]$Path,
    [string[]]$Required,
    [string[]]$Optional = @()
) {
    Assert-Object $Value $Path
    $actual = @($Value.PSObject.Properties.Name)
    foreach ($name in $Required) {
        if ($actual -cnotcontains $name) { Fail $Path "missing required property '$name'." }
    }
    foreach ($name in $actual) {
        if ($Required -cnotcontains $name -and $Optional -cnotcontains $name) {
            Fail $Path "unknown property '$name'."
        }
    }
}

function Items([object]$Value, [string]$Path) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -isnot [System.Collections.IEnumerable]) {
        Fail $Path 'expected a JSON array.'
    }
    return @($Value)
}

function Text([object]$Value, [string]$Path, [int]$Maximum = 2000, [switch]$AllowEmpty) {
    if ($Value -isnot [string]) { Fail $Path 'expected a string.' }
    if (-not $AllowEmpty -and [string]::IsNullOrWhiteSpace($Value)) { Fail $Path 'must not be empty.' }
    if ($Value.Length -gt $Maximum) { Fail $Path "exceeds the $Maximum-character bound." }
    return [string]$Value
}

function PublicText([object]$Value, [string]$Path, [int]$Maximum = 2000) {
    $text = Text $Value $Path $Maximum
    if ($text -match '[\x00-\x08\x0B\x0C\x0E-\x1F]' -or
        $text -match '(?i)(?:[A-Z]:[\\/]|/(?:home|Users)/)' -or
        $text -match '(?i)https://[^\s"'']+\.openai\.azure\.com' -or
        $text -match '(?i)SENTINEL[-_:]' -or
        $text -match '(?i)(?:api[_ -]?key|client[_ -]?secret|access[_ -]?token|password)\s*[:=]') {
        Fail $Path 'contains text that is not safe for a public receipt.'
    }
    return $text
}

function Identifier([object]$Value, [string]$Path, [int]$Maximum = 100) {
    $text = Text $Value $Path $Maximum
    if ($text -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._:/+-]*$') {
        Fail $Path 'is not a safe identifier.'
    }
    if ($text -match '^(?i:https?[:/]|[A-Z]:/)') { Fail $Path 'must not be a URL or rooted path.' }
    return $text
}

function CommitSha([object]$Value, [string]$Path) {
    $text = Text $Value $Path 40
    if ($text -cnotmatch '^[a-f0-9]{40}$') { Fail $Path 'must be a full lower-case 40-hex Git commit.' }
    return $text
}

function EnumValue([object]$Value, [string]$Path, [string[]]$Allowed) {
    $text = Text $Value $Path 100
    if ($Allowed -cnotcontains $text) {
        Fail $Path "unknown value '$text'."
    }
    return $text
}

function PrivateModelId([object]$Value, [string]$Path) {
    $label = Identifier $Value $Path 80
    if ($label -match '^(?i:sk-|pk-|eyJ|api[-_]?key|access[-_]?token|client[-_]?secret)') {
        Fail $Path 'resembles credential material rather than a model identifier.'
    }
    return $label
}

function PublicDefinitionVersion(
    [object]$Value,
    [string]$Path,
    [string]$Plan,
    [string]$JudgeModelId,
    [string[]]$ScenarioIds,
    [string]$RubricHash,
    [double]$PassThreshold,
    [long]$JudgeMaxOutputTokens) {
    $version = Identifier $Value $Path 1000
    if ($Plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow')) {
        $thresholdBits = '{0:X16}' -f [BitConverter]::DoubleToInt64Bits($PassThreshold)
        $expected = "1.1.0+cases.$($ScenarioIds -join '.').rubric.$($RubricHash.Substring(0, 16)).threshold.$thresholdBits.judge.$JudgeModelId.tokens.$JudgeMaxOutputTokens"
        if ($version -cne $expected) {
            Fail $Path 'does not match the deterministic use-case definition identity.'
        }
        $privateSegment = ".judge.$JudgeModelId.tokens."
        $version = $version.Replace(
            $privateSegment, '.judge.configured-model.tokens.', [StringComparison]::Ordinal)
    }
    return PublicText $version $Path 1000
}

function Boolean([object]$Value, [string]$Path) {
    if ($Value -isnot [bool]) { Fail $Path 'expected a Boolean.' }
    return [bool]$Value
}

function Integer([object]$Value, [string]$Path, [long]$Minimum = 0, [long]$Maximum = 2147483647) {
    if ($Value -isnot [byte] -and $Value -isnot [sbyte] -and $Value -isnot [int16] -and
        $Value -isnot [uint16] -and $Value -isnot [int32] -and $Value -isnot [uint32] -and
        $Value -isnot [int64]) {
        Fail $Path 'expected an integer.'
    }
    $number = [long]$Value
    if ($number -lt $Minimum -or $number -gt $Maximum) {
        Fail $Path "must be between $Minimum and $Maximum."
    }
    return $number
}

function Number([object]$Value, [string]$Path, [double]$Minimum, [double]$Maximum) {
    if ($Value -is [bool] -or $Value -isnot [ValueType]) { Fail $Path 'expected a number.' }
    try { $number = [double]$Value } catch { Fail $Path 'expected a number.' }
    if ([double]::IsNaN($number) -or [double]::IsInfinity($number) -or
        $number -lt $Minimum -or $number -gt $Maximum) {
        Fail $Path "must be a finite number between $Minimum and $Maximum."
    }
    return $number
}

function OptionalNumber([object]$Value, [string]$Name, [string]$Path, [double]$Minimum, [double]$Maximum) {
    if (-not (Has-Property $Value $Name)) { return $null }
    return (Number (Property $Value $Name) "$Path.$Name" $Minimum $Maximum)
}

function OptionalBoolean([object]$Value, [string]$Name, [string]$Path) {
    if (-not (Has-Property $Value $Name)) { return $null }
    return (Boolean (Property $Value $Name) "$Path.$Name")
}

function Timestamp([object]$Value, [string]$Path) {
    try {
        if ($Value -is [DateTimeOffset]) { return [DateTimeOffset]$Value }
        if ($Value -is [DateTime]) { return [DateTimeOffset]$Value }
        $text = Text $Value $Path 80
        return [DateTimeOffset]::Parse($text, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None)
    } catch {
        Fail $Path 'is not a valid round-trip DateTimeOffset value.'
    }
}

function Assert-StringArray([object]$Value, [string]$Path, [int]$MaximumItemLength = 4000) {
    $index = 0
    foreach ($item in (Items $Value $Path)) {
        [void](Text $item "$Path[$index]" $MaximumItemLength -AllowEmpty)
        $index++
    }
}

function Census([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('measured', 'notApplicable', 'notMeasured', 'total')
    $measured = Integer (Property $Value 'measured') "$Path.measured"
    $notApplicable = Integer (Property $Value 'notApplicable') "$Path.notApplicable"
    $notMeasured = Integer (Property $Value 'notMeasured') "$Path.notMeasured"
    $total = Integer (Property $Value 'total') "$Path.total"
    if ($total -ne $measured + $notApplicable + $notMeasured) {
        Fail $Path 'total does not equal measured + notApplicable + notMeasured.'
    }
    return [ordered]@{
        measured = $measured
        notApplicable = $notApplicable
        notMeasured = $notMeasured
        total = $total
    }
}

function Reliability([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('measurement', 'successes', 'total') @('estimate', 'lower', 'upper')
    $measurement = EnumValue (Property $Value 'measurement') "$Path.measurement" @('measured', 'notMeasured')
    $successes = Integer (Property $Value 'successes') "$Path.successes"
    $total = Integer (Property $Value 'total') "$Path.total"
    if ($successes -gt $total) { Fail $Path 'successes exceeds total.' }
    $estimate = OptionalNumber $Value 'estimate' $Path 0 1
    $lower = OptionalNumber $Value 'lower' $Path 0 1
    $upper = OptionalNumber $Value 'upper' $Path 0 1
    if ($measurement -ceq 'measured') {
        if ($total -eq 0 -or $null -eq $estimate -or $null -eq $lower -or $null -eq $upper) {
            Fail $Path 'a measured reliability requires a non-zero total and all three estimates.'
        }
        if ($lower -gt $estimate -or $estimate -gt $upper) { Fail $Path 'Wilson bounds are inconsistent.' }
        if ([Math]::Abs($estimate - ($successes / [double]$total)) -gt 0.000000000001) {
            Fail $Path 'estimate does not equal successes / total.'
        }
        $z = 1.959963984540054
        $zSquared = $z * $z
        $proportion = $successes / [double]$total
        $denominator = 1 + $zSquared / $total
        $center = ($proportion + $zSquared / (2 * $total)) / $denominator
        $margin = $z * [Math]::Sqrt(($proportion * (1 - $proportion) / $total) +
            ($zSquared / (4 * $total * $total))) / $denominator
        $expectedLower = [Math]::Max(0.0, [double]($center - $margin))
        $expectedUpper = [Math]::Min(1.0, [double]($center + $margin))
        if ([Math]::Abs($lower - $expectedLower) -gt 0.000000000001 -or
            [Math]::Abs($upper - $expectedUpper) -gt 0.000000000001) {
            Fail $Path 'Wilson bounds do not match the canonical 95% calculation.'
        }
    } elseif ($total -ne 0 -or $successes -ne 0 -or $null -ne $estimate -or $null -ne $lower -or $null -ne $upper) {
        Fail $Path 'notMeasured reliability must have a zero denominator and no estimates.'
    }
    $result = [ordered]@{
        measurement = $measurement
        successes = $successes
        total = $total
        estimate = $estimate
        lower = $lower
        upper = $upper
    }
    return $result
}

function Usage([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('status') @('modelCalls', 'inputTokens', 'outputTokens', 'totalTokens', 'estimatedCostUsd')
    $status = EnumValue (Property $Value 'status') "$Path.status" @('not-reported', 'measured-zero', 'measured', 'lower-bound')
    $result = [ordered]@{
        status = $status
        modelCalls = $null
        inputTokens = $null
        outputTokens = $null
        totalTokens = $null
        estimatedCostUsd = $null
    }
    foreach ($name in @('modelCalls', 'inputTokens', 'outputTokens', 'totalTokens')) {
        if (Has-Property $Value $name) { $result[$name] = Integer (Property $Value $name) "$Path.$name" 0 ([long]::MaxValue) }
    }
    if (Has-Property $Value 'estimatedCostUsd') {
        $result.estimatedCostUsd = Number (Property $Value 'estimatedCostUsd') "$Path.estimatedCostUsd" 0 ([double]::MaxValue)
    }
    $input = if ($result.Contains('inputTokens')) { $result.inputTokens } else { $null }
    $output = if ($result.Contains('outputTokens')) { $result.outputTokens } else { $null }
    $total = if ($result.Contains('totalTokens')) { $result.totalTokens } else { $null }
    if ($null -ne $input -and $null -ne $output -and $null -ne $total -and $input + $output -ne $total) {
        Fail $Path 'inputTokens + outputTokens does not equal totalTokens.'
    }
    if ($null -ne $input -and $null -ne $total -and $input -gt $total -or
        $null -ne $output -and $null -ne $total -and $output -gt $total) {
        Fail $Path 'a component token count exceeds totalTokens.'
    }
    if ($status -ceq 'not-reported' -and ($null -ne $result.modelCalls -or $null -ne $input -or
        $null -ne $output -or $null -ne $total -or $null -ne $result.estimatedCostUsd)) {
        Fail $Path 'not-reported usage contains measured amounts.'
    }
    if ($status -ceq 'measured' -and ($null -eq $total -or $total -le 0)) { Fail $Path 'measured usage requires positive totalTokens.' }
    if ($status -ceq 'measured-zero' -and (($total ?? 0) -ne 0 -or ($input ?? 0) -ne 0 -or ($output ?? 0) -ne 0 -or
        (($result.estimatedCostUsd ?? 0) -ne 0))) { Fail $Path 'measured-zero usage contains a non-zero amount.' }
    return $result
}

function Failure([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('code', 'detail') @('scenarioId', 'armId', 'repetition', 'checkKey')
    $code = EnumValue (Property $Value 'code') "$Path.code" @(
        'configurationUnavailable', 'cancelled', 'subjectProviderFailure', 'subjectExecutionFailed',
        'judgeExecutionFailed', 'benchmarkExecutionFailed', 'safetyExecutionFailed', 'subjectModelStageUnusable')
    # Validate, then deliberately replace free-form source detail with a stable public diagnostic.
    [void](Text (Property $Value 'detail') "$Path.detail" 320 -AllowEmpty)
    $details = @{
        configurationUnavailable = 'Required live-model configuration was unavailable.'
        cancelled = 'Execution was cancelled; any completed sanitized receipts were retained.'
        subjectProviderFailure = 'A subject-model provider request failed or was cancelled.'
        subjectExecutionFailed = 'The subject did not complete successfully.'
        judgeExecutionFailed = 'The judge did not produce a complete criterion verdict.'
        benchmarkExecutionFailed = 'The benchmark stopped before all planned evidence was measured.'
        safetyExecutionFailed = 'The safety scan did not produce a complete, fully measured result.'
        subjectModelStageUnusable = 'A required workflow model stage selected its bounded fallback after unusable model output.'
    }
    $result = [ordered]@{ code = $code; detail = $details[$code] }
    if (Has-Property $Value 'scenarioId') { $result.scenarioId = Identifier (Property $Value 'scenarioId') "$Path.scenarioId" }
    if (Has-Property $Value 'armId') { $result.armId = Identifier (Property $Value 'armId') "$Path.armId" }
    if (Has-Property $Value 'repetition') { $result.repetition = Integer (Property $Value 'repetition') "$Path.repetition" 1 }
    if (Has-Property $Value 'checkKey') { $result.checkKey = Identifier (Property $Value 'checkKey') "$Path.checkKey" }
    return $result
}

function Assert-Tools([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('journalObserved', 'toolNames', 'executed', 'completed', 'failed', 'cancelled',
        'unknownNameCount', 'calls', 'unreconciledCount')
    [void](Boolean (Property $Value 'journalObserved') "$Path.journalObserved")
    Assert-StringArray (Property $Value 'toolNames') "$Path.toolNames" 100
    foreach ($name in @('executed', 'completed', 'failed', 'cancelled', 'unknownNameCount', 'unreconciledCount')) {
        [void](Integer (Property $Value $name) "$Path.$name")
    }
    $index = 0
    foreach ($call in (Items (Property $Value 'calls') "$Path.calls")) {
        $callPath = "$Path.calls[$index]"
        Assert-Shape $call $callPath @('operationId', 'toolName', 'status', 'arguments')
        [void](Text (Property $call 'operationId') "$callPath.operationId" 200)
        [void](Text (Property $call 'toolName') "$callPath.toolName" 100)
        [void](EnumValue (Property $call 'status') "$callPath.status" @('completed', 'failed', 'cancelled', 'incomplete', 'unreconciled'))
        $argumentIndex = 0
        foreach ($argument in (Items (Property $call 'arguments') "$callPath.arguments")) {
            $argumentPath = "$callPath.arguments[$argumentIndex]"
            Assert-Shape $argument $argumentPath @('name', 'value')
            [void](Text (Property $argument 'name') "$argumentPath.name" 100)
            [void](Text (Property $argument 'value') "$argumentPath.value" 4000 -AllowEmpty)
            $argumentIndex++
        }
        $index++
    }
}

function Project-WorkflowEvidence([object]$Value, [string]$Path, [string]$SchemaVersion) {
    $baseProperties = @('executors', 'routes', 'discoveryRounds', 'maximumRounds', 'superSteps', 'stopReason',
        'looped', 'failureCount', 'degradationCount', 'degradationKinds', 'unknownExecutorCount', 'unknownRouteCount')
    $providerProperties = @('providerStages', 'providerFailedAttemptCount',
        'recoveredProviderFailedAttemptCount', 'terminalProviderStageCount')
    if ($SchemaVersion -ceq '1.4') {
        Assert-Shape $Value $Path ($baseProperties + $providerProperties)
    } else {
        Assert-Shape $Value $Path $baseProperties
    }
    $allowedExecutors = @('InterestMapper', 'Discovery', 'CoverageReviewer', 'Ranker', 'Presenter')
    $executors = @()
    $index = 0
    foreach ($executor in (Items (Property $Value 'executors') "$Path.executors")) {
        $executorPath = "$Path.executors[$index]"
        Assert-Shape $executor $executorPath @('executorId', 'executionCount')
        $executors += ,[ordered]@{
            executorId = EnumValue (Property $executor 'executorId') "$executorPath.executorId" $allowedExecutors
            executionCount = Integer (Property $executor 'executionCount') "$executorPath.executionCount"
        }
        $index++
    }
    $routes = @()
    $index = 0
    foreach ($route in (Items (Property $Value 'routes') "$Path.routes")) {
        $routes += EnumValue $route "$Path.routes[$index]" @('map-to-discovery', 'discovery-to-review',
            'review-to-more-discovery', 'review-to-ranker', 'ranker-to-presenter')
        $index++
    }
    $allowedDegradationKinds = @()
    foreach ($executorId in ($allowedExecutors + @('unknown'))) {
        foreach ($suffix in @('fallback', 'degradation', 'attempt-unusable', 'model-failure', 'provider-recovered', 'final-fallback',
            'provider-unrecovered', 'provider-cancelled')) {
            $allowedDegradationKinds += "$executorId`:$suffix"
        }
    }
    $degradationKinds = @()
    $index = 0
    foreach ($kind in (Items (Property $Value 'degradationKinds') "$Path.degradationKinds")) {
        $degradationKinds += EnumValue $kind "$Path.degradationKinds[$index]" $allowedDegradationKinds
        $index++
    }
    $result = [ordered]@{
        executors = $executors
        routes = $routes
        discoveryRounds = Integer (Property $Value 'discoveryRounds') "$Path.discoveryRounds"
        maximumRounds = Integer (Property $Value 'maximumRounds') "$Path.maximumRounds"
        superSteps = Integer (Property $Value 'superSteps') "$Path.superSteps"
        stopReason = EnumValue (Property $Value 'stopReason') "$Path.stopReason" @('None', 'CoverageSufficient',
            'GapsRemain', 'RoundLimitReached', 'NoProgress', 'GapsUnresolvable', 'not-measured')
        looped = Boolean (Property $Value 'looped') "$Path.looped"
        failureCount = Integer (Property $Value 'failureCount') "$Path.failureCount"
        degradationCount = Integer (Property $Value 'degradationCount') "$Path.degradationCount"
        degradationKinds = $degradationKinds
        unknownExecutorCount = Integer (Property $Value 'unknownExecutorCount') "$Path.unknownExecutorCount"
        unknownRouteCount = Integer (Property $Value 'unknownRouteCount') "$Path.unknownRouteCount"
    }
    if ($SchemaVersion -ceq '1.3') { return $result }

    $providerStages = @()
    $seenProviderStages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failedAttempts = 0L
    $recoveredFailedAttempts = 0L
    $terminalStages = 0L
    $totalAttempts = 0L
    $index = 0
    foreach ($stage in (Items (Property $Value 'providerStages') "$Path.providerStages")) {
        $stagePath = "$Path.providerStages[$index]"
        Assert-Shape $stage $stagePath @('executorId', 'attemptCount', 'responseCount', 'unusableAttemptCount', 'failedAttemptCount',
            'cancelledAttemptCount', 'status', 'lastUnusableAttemptNumber', 'lastUsableResponseAttemptNumber')
        $executorId = EnumValue (Property $stage 'executorId') "$stagePath.executorId" @(
            'InterestMapper', 'CoverageReviewer', 'Ranker', 'Presenter')
        if (-not $seenProviderStages.Add($executorId)) { Fail $stagePath 'duplicates a model-backed executor.' }
        $attempts = Integer (Property $stage 'attemptCount') "$stagePath.attemptCount"
        $responses = Integer (Property $stage 'responseCount') "$stagePath.responseCount"
        $unusable = Integer (Property $stage 'unusableAttemptCount') "$stagePath.unusableAttemptCount"
        $failures = Integer (Property $stage 'failedAttemptCount') "$stagePath.failedAttemptCount"
        $cancellations = Integer (Property $stage 'cancelledAttemptCount') "$stagePath.cancelledAttemptCount"
        $lastUnusable = Integer (Property $stage 'lastUnusableAttemptNumber') "$stagePath.lastUnusableAttemptNumber"
        $lastUsable = Integer (Property $stage 'lastUsableResponseAttemptNumber') "$stagePath.lastUsableResponseAttemptNumber"
        $status = EnumValue (Property $stage 'status') "$stagePath.status" @(
            'completed', 'recovered', 'finalFallback', 'unrecovered', 'cancelled')
        if ($attempts -le 0 -or $unusable -gt $attempts -or $failures -gt $unusable -or
            $attempts -lt $responses + $failures + $cancellations -or
            $attempts -gt $responses + $cancellations + $unusable -or
            $unusable + $cancellations -gt $attempts -or
            (($unusable -eq 0) -ne ($lastUnusable -eq 0)) -or
            ($lastUsable -gt 0 -and $responses -eq 0)) {
            Fail $stagePath 'contains an impossible typed attempt census.'
        }
        $consistent = switch ($status) {
            'completed' { $responses -eq $attempts -and $unusable -eq 0 -and $failures -eq 0 -and
                $cancellations -eq 0 -and $lastUnusable -eq 0 -and $lastUsable -gt 0 }
            'recovered' { $attempts -gt $unusable -and $unusable -gt 0 -and $responses -gt 0 -and
                $cancellations -eq 0 -and $lastUnusable -gt 0 -and $lastUsable -gt $lastUnusable }
            'finalFallback' { $unusable -gt 0 -and $cancellations -eq 0 -and $lastUnusable -gt 0 }
            'unrecovered' { $true }
            'cancelled' { $cancellations -gt 0 }
        }
        if (-not $consistent) { Fail $stagePath 'status contradicts the typed attempt census.' }
        $providerStages += ,[ordered]@{
            executorId = $executorId
            attemptCount = $attempts
            responseCount = $responses
            unusableAttemptCount = $unusable
            failedAttemptCount = $failures
            cancelledAttemptCount = $cancellations
            status = $status
            lastUnusableAttemptNumber = $lastUnusable
            lastUsableResponseAttemptNumber = $lastUsable
        }
        $failedAttempts += $failures
        if ($status -ceq 'recovered') { $recoveredFailedAttempts += $failures }
        if ($status -in @('finalFallback', 'unrecovered', 'cancelled')) { $terminalStages++ }
        $totalAttempts += $attempts
        $index++
    }
    $seenLastAttemptNumbers = [Collections.Generic.HashSet[long]]::new()
    $index = 0
    foreach ($stage in $providerStages) {
        foreach ($name in @('lastUnusableAttemptNumber', 'lastUsableResponseAttemptNumber')) {
            $attemptNumber = [long]$stage[$name]
            if ($attemptNumber -gt 0 -and ($attemptNumber -gt $totalAttempts -or
                -not $seenLastAttemptNumbers.Add($attemptNumber))) {
                Fail "$Path.providerStages[$index].$name" 'must identify one unique attempt within the global attempt census.'
            }
        }
        $index++
    }
    $reportedFailed = Integer (Property $Value 'providerFailedAttemptCount') "$Path.providerFailedAttemptCount"
    $reportedRecovered = Integer (Property $Value 'recoveredProviderFailedAttemptCount') "$Path.recoveredProviderFailedAttemptCount"
    $reportedTerminal = Integer (Property $Value 'terminalProviderStageCount') "$Path.terminalProviderStageCount"
    if ($reportedFailed -ne $failedAttempts -or $reportedRecovered -ne $recoveredFailedAttempts -or
        $reportedTerminal -ne $terminalStages) {
        Fail $Path 'provider-stage aggregate counts do not match providerStages.'
    }
    $result['providerStages'] = $providerStages
    $result['providerFailedAttemptCount'] = $reportedFailed
    $result['recoveredProviderFailedAttemptCount'] = $reportedRecovered
    $result['terminalProviderStageCount'] = $reportedTerminal
    return $result
}

function Check([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('key', 'name', 'measurement') @('score', 'passed')
    $measurement = EnumValue (Property $Value 'measurement') "$Path.measurement" @('measured', 'notApplicable', 'notMeasured')
    $result = [ordered]@{
        key = Identifier (Property $Value 'key') "$Path.key"
        name = PublicText (Property $Value 'name') "$Path.name" 240
        measurement = $measurement
    }
    if (Has-Property $Value 'score') { $result.score = Number (Property $Value 'score') "$Path.score" 0 1 }
    if (Has-Property $Value 'passed') { $result.passed = Boolean (Property $Value 'passed') "$Path.passed" }
    if ($measurement -ceq 'measured' -and (-not $result.Contains('score') -or -not $result.Contains('passed'))) {
        Fail $Path 'a measured check requires a score and verdict.'
    }
    if ($measurement -cne 'measured' -and ($result.Contains('score') -or $result.Contains('passed'))) {
        Fail $Path 'an unmeasured check cannot carry a score or verdict.'
    }
    return $result
}

function Criterion([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('id', 'measurement', 'explanation') @('met')
    $measurement = EnumValue (Property $Value 'measurement') "$Path.measurement" @('measured', 'notApplicable', 'notMeasured')
    # Explanation is structurally validated but intentionally not projected.
    [void](Text (Property $Value 'explanation') "$Path.explanation" 8000 -AllowEmpty)
    $result = [ordered]@{
        id = Identifier (Property $Value 'id') "$Path.id"
        measurement = $measurement
    }
    if (Has-Property $Value 'met') { $result.met = Boolean (Property $Value 'met') "$Path.met" }
    if ($measurement -ceq 'measured' -and -not $result.Contains('met')) { Fail $Path 'a measured criterion requires a verdict.' }
    if ($measurement -cne 'measured' -and $result.Contains('met')) { Fail $Path 'an unmeasured criterion cannot carry a verdict.' }
    return $result
}

function ArmSummary([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('armId', 'architecture', 'repetitions', 'checks')
    $checks = @()
    $index = 0
    foreach ($check in (Items (Property $Value 'checks') "$Path.checks")) {
        $checkPath = "$Path.checks[$index]"
        Assert-Shape $check $checkPath @('key', 'name', 'census', 'reliability')
        $checks += ,[ordered]@{
            key = Identifier (Property $check 'key') "$checkPath.key"
            name = PublicText (Property $check 'name') "$checkPath.name" 240
            census = Census (Property $check 'census') "$checkPath.census"
            reliability = Reliability (Property $check 'reliability') "$checkPath.reliability"
        }
        $index++
    }
    return [ordered]@{
        armId = Identifier (Property $Value 'armId') "$Path.armId"
        architecture = EnumValue (Property $Value 'architecture') "$Path.architecture" @('agent', 'workflow')
        repetitions = Integer (Property $Value 'repetitions') "$Path.repetitions" 1
        checks = $checks
    }
}

function ScenarioAcceptance([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('scenarioId', 'personaId', 'armId', 'architecture', 'census', 'reliability',
        'confidenceLevel', 'minimumLowerBound') @('passed')
    $result = [ordered]@{
        scenarioId = Identifier (Property $Value 'scenarioId') "$Path.scenarioId"
        personaId = Identifier (Property $Value 'personaId') "$Path.personaId"
        armId = Identifier (Property $Value 'armId') "$Path.armId"
        architecture = EnumValue (Property $Value 'architecture') "$Path.architecture" @('agent', 'workflow')
        census = Census (Property $Value 'census') "$Path.census"
        reliability = Reliability (Property $Value 'reliability') "$Path.reliability"
        confidenceLevel = Number (Property $Value 'confidenceLevel') "$Path.confidenceLevel" 0 1
        minimumLowerBound = Number (Property $Value 'minimumLowerBound') "$Path.minimumLowerBound" 0 1
        passed = $null
    }
    if (Has-Property $Value 'passed') { $result.passed = Boolean (Property $Value 'passed') "$Path.passed" }
    return $result
}

function Comparison([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('checkKey', 'checkName', 'referenceArm', 'challengerArm', 'wins', 'losses', 'ties',
        'effectiveN', 'cases', 'totalRepObservations', 'repCollapse', 'census', 'underpoweredByConstruction', 'undecidable') `
        @('pValue', 'minimumAttainableP', 'meanDelta', 'meanRepetitionsPerCase')
    $result = [ordered]@{
        checkKey = Identifier (Property $Value 'checkKey') "$Path.checkKey"
        checkName = PublicText (Property $Value 'checkName') "$Path.checkName" 240
        referenceArm = Identifier (Property $Value 'referenceArm') "$Path.referenceArm"
        challengerArm = Identifier (Property $Value 'challengerArm') "$Path.challengerArm"
        wins = Integer (Property $Value 'wins') "$Path.wins"
        losses = Integer (Property $Value 'losses') "$Path.losses"
        ties = Integer (Property $Value 'ties') "$Path.ties"
        effectiveN = Integer (Property $Value 'effectiveN') "$Path.effectiveN"
        cases = Integer (Property $Value 'cases') "$Path.cases"
        totalRepObservations = Integer (Property $Value 'totalRepObservations') "$Path.totalRepObservations"
        repCollapse = EnumValue (Property $Value 'repCollapse') "$Path.repCollapse" @('All')
        census = Census (Property $Value 'census') "$Path.census"
        underpoweredByConstruction = Boolean (Property $Value 'underpoweredByConstruction') "$Path.underpoweredByConstruction"
        undecidable = Boolean (Property $Value 'undecidable') "$Path.undecidable"
        pValue = $null
        minimumAttainableP = $null
        meanDelta = $null
        meanRepetitionsPerCase = $null
    }
    foreach ($name in @('pValue', 'minimumAttainableP')) {
        if (Has-Property $Value $name) { $result[$name] = Number (Property $Value $name) "$Path.$name" 0 1 }
    }
    if (Has-Property $Value 'meanDelta') { $result.meanDelta = Number (Property $Value 'meanDelta') "$Path.meanDelta" -1 1 }
    if (Has-Property $Value 'meanRepetitionsPerCase') {
        $result.meanRepetitionsPerCase = Number (Property $Value 'meanRepetitionsPerCase') "$Path.meanRepetitionsPerCase" 0 1000000
    }
    if ($result.wins + $result.losses -ne $result.effectiveN) { Fail $Path 'effectiveN does not equal wins + losses.' }
    return $result
}

function SafetyProbeDiagnostic([string]$Outcome, [string]$ErrorKind) {
    if ($ErrorKind -ceq 'timeout') { return 'The probe reached its configured timeout.' }
    if ($ErrorKind -ceq 'transport') { return 'The remote model transport failed.' }
    if ($ErrorKind -ceq 'execution') {
        return 'An unexpected probe execution fault occurred; underlying provider and exception detail was deliberately suppressed because IncludeEvidence=false.'
    }
    if ($Outcome -ceq 'inconclusive') { return 'AgentEval could not decide this probe.' }
    return 'No probe execution error.'
}

function Safety([object]$Value, [string]$Path) {
    Assert-Shape $Value $Path @('target', 'measurement', 'total', 'resisted', 'compromised', 'inconclusive', 'errored',
        'truncated', 'skipped', 'attacks', 'probes', 'subjectUsage', 'judgeUsage') @('passed')
    $attacks = @()
    $attackNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $index = 0
    foreach ($attack in (Items (Property $Value 'attacks') "$Path.attacks")) {
        $attackPath = "$Path.attacks[$index]"
        Assert-Shape $attack $attackPath @('attack', 'owaspId', 'total', 'resisted', 'compromised', 'inconclusive', 'errored')
        $attackName = EnumValue (Property $attack 'attack') "$attackPath.attack" @('Jailbreak', 'SystemPromptExtraction')
        if (-not $attackNames.Add($attackName)) { Fail $attackPath 'duplicates an attack summary.' }
        $owaspId = EnumValue (Property $attack 'owaspId') "$attackPath.owaspId" @('LLM01', 'LLM07')
        if (($attackName -ceq 'Jailbreak' -and $owaspId -cne 'LLM01') -or
            ($attackName -ceq 'SystemPromptExtraction' -and $owaspId -cne 'LLM07')) {
            Fail $attackPath 'attack and OWASP id do not match.'
        }
        $row = [ordered]@{ attack = $attackName; owaspId = $owaspId }
        foreach ($name in @('total', 'resisted', 'compromised', 'inconclusive', 'errored')) {
            $row[$name] = Integer (Property $attack $name) "$attackPath.$name"
        }
        if ($row.total -ne $row.resisted + $row.compromised + $row.inconclusive -or $row.errored -gt $row.inconclusive) {
            Fail $attackPath 'outcome census is inconsistent.'
        }
        $attacks += ,$row
        $index++
    }
    $probes = @()
    $index = 0
    foreach ($probe in (Items (Property $Value 'probes') "$Path.probes")) {
        $probePath = "$Path.probes[$index]"
        Assert-Shape $probe $probePath @('attack', 'probeId', 'outcome', 'errorKind', 'severity', 'fidelity', 'technique', 'diagnostic') @('failure')
        $attack = EnumValue (Property $probe 'attack') "$probePath.attack" @('Jailbreak', 'SystemPromptExtraction')
        $probeId = Identifier (Property $probe 'probeId') "$probePath.probeId"
        if (($attack -ceq 'Jailbreak' -and $probeId -cnotmatch '^JB-[0-9]{3}$') -or
            ($attack -ceq 'SystemPromptExtraction' -and $probeId -cnotmatch '^SPE-[0-9]{3}$')) {
            Fail $probePath 'probe id and attack do not match.'
        }
        $outcome = EnumValue (Property $probe 'outcome') "$probePath.outcome" @('compromised', 'resisted', 'inconclusive')
        $errorKind = EnumValue (Property $probe 'errorKind') "$probePath.errorKind" @('none', 'timeout', 'transport', 'execution')
        if ($errorKind -cne 'none' -and $outcome -cne 'inconclusive') { Fail $probePath 'an errored probe must be inconclusive.' }
        $diagnostic = SafetyProbeDiagnostic $outcome $errorKind
        if ((Property $probe 'diagnostic') -cne $diagnostic) { Fail "$probePath.diagnostic" 'is not the canonical redacted diagnostic.' }
        $row = [ordered]@{
            attack = $attack
            probeId = $probeId
            outcome = $outcome
            errorKind = $errorKind
            severity = EnumValue (Property $probe 'severity') "$probePath.severity" @('High', 'Medium')
            fidelity = EnumValue (Property $probe 'fidelity') "$probePath.fidelity" @('Verbal')
            technique = EnumValue (Property $probe 'technique') "$probePath.technique" @('roleplay', 'direct_request')
            diagnostic = $diagnostic
            failure = $null
        }
        if ($errorKind -ceq 'none') {
            if (Has-Property $probe 'failure') { Fail $probePath 'a successful probe cannot carry a failure.' }
        } else {
            if (-not (Has-Property $probe 'failure')) { Fail $probePath 'an errored probe requires a typed failure.' }
            $failure = Property $probe 'failure'
            Assert-Shape $failure "$probePath.failure" @('stage', 'code', 'detail')
            if ((Property $failure 'stage') -cne 'probe-execution' -or
                (Property $failure 'code') -cne $errorKind -or
                (Property $failure 'detail') -cne $diagnostic) {
                Fail "$probePath.failure" 'is not the canonical typed probe failure.'
            }
            $row.failure = [ordered]@{ stage = 'probe-execution'; code = $errorKind; detail = $diagnostic }
        }
        $probes += ,$row
        $index++
    }
    $result = [ordered]@{
        target = Identifier (Property $Value 'target') "$Path.target"
        measurement = EnumValue (Property $Value 'measurement') "$Path.measurement" @('measured', 'notMeasured')
        total = Integer (Property $Value 'total') "$Path.total"
        resisted = Integer (Property $Value 'resisted') "$Path.resisted"
        compromised = Integer (Property $Value 'compromised') "$Path.compromised"
        inconclusive = Integer (Property $Value 'inconclusive') "$Path.inconclusive"
        errored = Integer (Property $Value 'errored') "$Path.errored"
        truncated = Boolean (Property $Value 'truncated') "$Path.truncated"
        skipped = Integer (Property $Value 'skipped') "$Path.skipped"
        attacks = $attacks
        probes = $probes
        subjectUsage = Usage (Property $Value 'subjectUsage') "$Path.subjectUsage"
        judgeUsage = Usage (Property $Value 'judgeUsage') "$Path.judgeUsage"
        passed = $null
    }
    if (Has-Property $Value 'passed') { $result.passed = Boolean (Property $Value 'passed') "$Path.passed" }
    if ($result.total -ne $result.resisted + $result.compromised + $result.inconclusive -or
        $result.errored -gt $result.inconclusive -or $result.total -ne $probes.Count) {
        Fail $Path 'aggregate probe census is inconsistent.'
    }
    $attackTotal = 0; $attackResisted = 0; $attackCompromised = 0; $attackInconclusive = 0; $attackErrored = 0
    foreach ($attack in $attacks) {
        $attackProbes = @($probes | Where-Object attack -ceq $attack.attack)
        if ($attack.total -ne $attackProbes.Count -or
            $attack.resisted -ne @($attackProbes | Where-Object outcome -ceq 'resisted').Count -or
            $attack.compromised -ne @($attackProbes | Where-Object outcome -ceq 'compromised').Count -or
            $attack.inconclusive -ne @($attackProbes | Where-Object outcome -ceq 'inconclusive').Count -or
            $attack.errored -ne @($attackProbes | Where-Object errorKind -cne 'none').Count) {
            Fail "$Path.attacks" 'an attack census does not match its redacted probes.'
        }
        $attackTotal += $attack.total
        $attackResisted += $attack.resisted
        $attackCompromised += $attack.compromised
        $attackInconclusive += $attack.inconclusive
        $attackErrored += $attack.errored
    }
    if ($attackTotal -ne $result.total -or $attackResisted -ne $result.resisted -or
        $attackCompromised -ne $result.compromised -or $attackInconclusive -ne $result.inconclusive -or
        $attackErrored -ne $result.errored) {
        Fail "$Path.attacks" 'attack summaries do not add up to the aggregate safety census.'
    }
    return $result
}

function UsageRollup([object[]]$Usages, [string]$Path) {
    $statusCensus = [ordered]@{ notReported = 0; measuredZero = 0; measured = 0; lowerBound = 0 }
    [decimal]$modelCalls = 0; [decimal]$inputTokens = 0; [decimal]$outputTokens = 0; [decimal]$totalTokens = 0
    [double]$estimatedCost = 0
    $modelRows = 0; $inputRows = 0; $outputRows = 0; $tokenRows = 0; $costRows = 0
    foreach ($usage in $Usages) {
        switch ($usage.status) {
            'not-reported' { $statusCensus.notReported++ }
            'measured-zero' { $statusCensus.measuredZero++ }
            'measured' { $statusCensus.measured++ }
            'lower-bound' { $statusCensus.lowerBound++ }
            default { Fail $Path 'contains an unknown projected usage state.' }
        }
        if ($null -ne $usage.modelCalls) { $modelRows++; $modelCalls += $usage.modelCalls }
        if ($null -ne $usage.inputTokens) { $inputRows++; $inputTokens += $usage.inputTokens }
        if ($null -ne $usage.outputTokens) { $outputRows++; $outputTokens += $usage.outputTokens }
        if ($null -ne $usage.totalTokens) { $tokenRows++; $totalTokens += $usage.totalTokens }
        if ($null -ne $usage.estimatedCostUsd) { $costRows++; $estimatedCost += $usage.estimatedCostUsd }
    }
    foreach ($amount in @($modelCalls, $inputTokens, $outputTokens, $totalTokens)) {
        if ($amount -gt [long]::MaxValue) { Fail $Path 'aggregate usage exceeds Int64 capacity.' }
    }
    if ([double]::IsInfinity($estimatedCost) -or [double]::IsNaN($estimatedCost)) {
        Fail $Path 'aggregate estimated cost is not finite.'
    }
    return [ordered]@{
        evidenceRows = $Usages.Count
        statusCensus = $statusCensus
        reportedModelCallRows = $modelRows
        reportedModelCalls = if ($modelRows -eq 0) { $null } else { [long]$modelCalls }
        reportedInputTokenRows = $inputRows
        reportedInputTokens = if ($inputRows -eq 0) { $null } else { [long]$inputTokens }
        reportedOutputTokenRows = $outputRows
        reportedOutputTokens = if ($outputRows -eq 0) { $null } else { [long]$outputTokens }
        reportedTotalTokenRows = $tokenRows
        reportedTotalTokens = if ($tokenRows -eq 0) { $null } else { [long]$totalTokens }
        reportedCostRows = $costRows
        estimatedCostUsd = if ($costRows -eq 0) { $null } else { $estimatedCost }
    }
}

function Html([object]$Value) {
    function EncodeHtml([object]$Text) { return [Net.WebUtility]::HtmlEncode([string]$Text) }
    # PowerShell reserves the built-in `h` alias for Get-History; shadow it only in this
    # function scope so the compact rendering calls below resolve to our encoder.
    Set-Alias -Name H -Value EncodeHtml -Scope Local
    function Display([object]$Text) { if ($null -eq $Text) { return 'not measured' }; return [string]$Text }
    function Probability([object]$Text) { if ($null -eq $Text) { return 'not measured' }; return ([double]$Text).ToString('0.000', [Globalization.CultureInfo]::InvariantCulture) }
    function Money([object]$Text) { if ($null -eq $Text) { return 'not reported' }; return '$' + ([double]$Text).ToString('0.000000', [Globalization.CultureInfo]::InvariantCulture) }
    function UsageText([object]$Usage) {
        return "$(H $Usage.status); calls $(H (Display $Usage.modelCalls)); tokens $(H (Display $Usage.totalTokens)); estimated cost $(H (Money $Usage.estimatedCostUsd))"
    }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.AppendLine('<!doctype html>')
    [void]$builder.AppendLine('<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">')
    [void]$builder.Append('<title>VITRINE live evidence · ').Append((H $Value.plan)).AppendLine('</title>')
    [void]$builder.AppendLine('<style>:root{color-scheme:dark;--bg:#0b1020;--panel:#141c31;--line:#33415f;--ink:#f3f6ff;--muted:#aebbd5;--accent:#6ee7c7;--bad:#ff9c9c}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.55 system-ui,-apple-system,Segoe UI,sans-serif}main{max-width:1160px;margin:auto;padding:34px 22px 70px}a{color:var(--accent)}h1{font-size:clamp(2rem,5vw,3.8rem);line-height:1.05;margin:.3rem 0}h2{margin-top:2.4rem}h3{margin:1.4rem 0 .4rem}.eyebrow,.muted{color:var(--muted)}.eyebrow{text-transform:uppercase;letter-spacing:.14em;font-weight:700}.panel{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:18px;margin:16px 0}.grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(210px,1fr));gap:12px}.metric{background:#0f1729;border:1px solid var(--line);border-radius:10px;padding:12px}.metric strong{display:block;font-size:1.25rem}table{width:100%;border-collapse:collapse;margin:10px 0 22px}th,td{text-align:left;vertical-align:top;border-bottom:1px solid var(--line);padding:9px 8px}th{color:var(--muted);font-size:.78rem;text-transform:uppercase;letter-spacing:.06em}.scroll{overflow-x:auto}.status{color:var(--accent);font-weight:800}.warning{border-left:4px solid #f7c873}code{overflow-wrap:anywhere}@media print{body{background:#fff;color:#111}.panel,.metric{background:#fff;border-color:#bbb}.muted,.eyebrow,th{color:#444}a{color:#075985}}</style></head><body><main>')
    [void]$builder.AppendLine('<p><a href="../index.html">← VITRINE documentation</a></p>')
    [void]$builder.AppendLine('<p class="eyebrow">Sanitized live-evaluation receipt</p>')
    [void]$builder.Append('<h1>').Append((H $Value.plan)).AppendLine('</h1>')
    [void]$builder.Append('<p class="muted">Session <code>').Append((H $Value.sessionId)).Append('</code> · source schema ').Append((H $Value.source.schemaVersion)).Append(' · source SHA-256 <code>').Append((H $Value.source.sha256)).AppendLine('</code></p>')
    [void]$builder.AppendLine('<div class="grid">')
    foreach ($metric in @(
        @('Terminal status', $Value.terminalStatus), @('Exit code', $Value.exitCode),
        @('Started (UTC)', $Value.startedAtUtc), @('Completed (UTC)', $Value.completedAtUtc),
        @('Planned subject calls', $Value.workload.plannedSubjectCalls),
        @('Planned judge evaluations', $Value.workload.plannedJudgeEvaluations),
        @('Reported subject tokens', (Display $Value.usageTotals.subject.reportedTotalTokens)),
        @('Subject estimated cost', (Money $Value.usageTotals.subject.estimatedCostUsd)),
        @('Reported judge tokens', (Display $Value.usageTotals.judge.reportedTotalTokens)),
        @('Judge estimated cost', (Money $Value.usageTotals.judge.estimatedCostUsd)))) {
        [void]$builder.Append('<div class="metric"><span class="muted">').Append((H $metric[0])).Append('</span><strong>').Append((H $metric[1])).AppendLine('</strong></div>')
    }
    [void]$builder.AppendLine('</div>')
    [void]$builder.AppendLine('<div class="panel warning"><strong>Publication boundary.</strong> This receipt contains allow-listed aggregate evidence only. Exact queries, evaluator prompts, expected answers, ground truth, model responses, tool arguments, judge explanations, provider errors, endpoints, credentials, canaries, absolute paths and run directories are deliberately absent. Counts describe this session, not a universal model claim.</div>')
    [void]$builder.AppendLine('<h2>Configuration</h2><div class="panel">')
    [void]$builder.Append('<p>Definition <code>').Append((H $Value.configuration.definitionKey)).Append('</code> ').Append((H $Value.configuration.definitionVersion)).Append(' · judge model <code>').Append((H $Value.configuration.judgeModelLabel)).Append('</code> · rubric SHA-256 <code>').Append((H $Value.configuration.judgeRubricSha256)).AppendLine('</code></p><ul>')
    foreach ($subject in $Value.configuration.subjects) {
        [void]$builder.Append('<li><code>').Append((H $subject.armId)).Append('</code> · ').Append((H $subject.architecture)).Append(' · model <code>').Append((H $subject.modelLabel)).Append('</code> · judge relation ').Append((H $subject.judgeSubjectRelation)).AppendLine('</li>')
    }
    [void]$builder.AppendLine('</ul></div>')
    if (@($Value.scenarios).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Scenarios</h2><div class="grid">')
        foreach ($scenario in $Value.scenarios) {
            [void]$builder.Append('<article class="panel"><h3>').Append((H $scenario.title)).Append('</h3><p>').Append((H $scenario.description)).Append('</p><p class="muted"><code>').Append((H $scenario.id)).Append('</code> · persona <code>').Append((H $scenario.personaId)).Append('</code> · criteria ').Append((H ($scenario.criterionIds -join ', '))).AppendLine('</p></article>')
        }
        [void]$builder.AppendLine('</div>')
    }
    if (@($Value.scenarioAcceptances).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Terminal per-scenario acceptance</h2><div class="scroll"><table><thead><tr><th>Scenario · arm</th><th>Census</th><th>Whole-trial success</th><th>95% Wilson interval</th><th>Floor</th><th>Decision</th></tr></thead><tbody>')
        foreach ($row in $Value.scenarioAcceptances) {
            [void]$builder.Append('<tr><td><code>').Append((H $row.scenarioId)).Append('</code><br>').Append((H $row.armId)).Append('</td><td>M ').Append((H $row.census.measured)).Append(' · N/A ').Append((H $row.census.notApplicable)).Append(' · NM ').Append((H $row.census.notMeasured)).Append('</td><td>').Append((H $row.reliability.successes)).Append('/').Append((H $row.reliability.total)).Append('</td><td>[').Append((H (Probability $row.reliability.lower))).Append(', ').Append((H (Probability $row.reliability.upper))).Append('</td><td>≥ ').Append((H (Probability $row.minimumLowerBound))).Append('</td><td class="status">').Append((H (Display (OptionalProperty $row 'passed')))).AppendLine('</td></tr>')
        }
        [void]$builder.AppendLine('</tbody></table></div>')
    }
    if (@($Value.arms).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Per-check diagnostic census</h2><p class="muted">These pooled rows explain each arm; the terminal policy for stochastic plans is the per-scenario table above.</p><div class="scroll"><table><thead><tr><th>Arm</th><th>Check</th><th>Census</th><th>Successes</th><th>Estimate</th><th>95% Wilson interval</th></tr></thead><tbody>')
        foreach ($arm in $Value.arms) {
            foreach ($check in $arm.checks) {
                [void]$builder.Append('<tr><td><code>').Append((H $arm.armId)).Append('</code></td><td><code>').Append((H $check.key)).Append('</code><br>').Append((H $check.name)).Append('</td><td>M ').Append((H $check.census.measured)).Append(' · N/A ').Append((H $check.census.notApplicable)).Append(' · NM ').Append((H $check.census.notMeasured)).Append('</td><td>').Append((H $check.reliability.successes)).Append('/').Append((H $check.reliability.total)).Append('</td><td>').Append((H (Probability $check.reliability.estimate))).Append('</td><td>[').Append((H (Probability $check.reliability.lower))).Append(', ').Append((H (Probability $check.reliability.upper))).AppendLine(']</td></tr>')
            }
        }
        [void]$builder.AppendLine('</tbody></table></div>')
    }
    if (@($Value.criteriaCensus).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Criterion census</h2><div class="scroll"><table><thead><tr><th>Scenario</th><th>Arm</th><th>Criterion</th><th>Observed</th><th>Measured</th><th>Met</th><th>Unmet</th><th>N/A</th><th>Not measured</th></tr></thead><tbody>')
        foreach ($row in $Value.criteriaCensus) {
            [void]$builder.Append('<tr><td><code>').Append((H $row.scenarioId)).Append('</code></td><td><code>').Append((H $row.armId)).Append('</code></td><td><code>').Append((H $row.criterionId)).Append('</code></td><td>').Append((H $row.observed)).Append('</td><td>').Append((H $row.measured)).Append('</td><td>').Append((H $row.met)).Append('</td><td>').Append((H $row.unmet)).Append('</td><td>').Append((H $row.notApplicable)).Append('</td><td>').Append((H $row.notMeasured)).AppendLine('</td></tr>')
        }
        [void]$builder.AppendLine('</tbody></table></div>')
    }
    if (@($Value.comparisons).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Paired comparisons</h2><div class="scroll"><table><thead><tr><th>Check</th><th>Reference → challenger</th><th>W/L/T</th><th>Effective n</th><th>p-value</th><th>Mean delta</th><th>Power note</th></tr></thead><tbody>')
        foreach ($row in $Value.comparisons) {
            [void]$builder.Append('<tr><td><code>').Append((H $row.checkKey)).Append('</code></td><td>').Append((H $row.referenceArm)).Append(' → ').Append((H $row.challengerArm)).Append('</td><td>').Append((H $row.wins)).Append('/').Append((H $row.losses)).Append('/').Append((H $row.ties)).Append('</td><td>').Append((H $row.effectiveN)).Append('</td><td>').Append((H (Probability (OptionalProperty $row 'pValue')))).Append('</td><td>').Append((H (Probability (OptionalProperty $row 'meanDelta')))).Append('</td><td>').Append($(if ($row.underpoweredByConstruction) { 'underpowered by construction' } elseif ($row.undecidable) { 'undecidable' } else { 'measured' })).AppendLine('</td></tr>')
        }
        [void]$builder.AppendLine('</tbody></table></div>')
    }
    if (@($Value.trials).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Trial usage and verdicts</h2>')
        foreach ($trial in $Value.trials) {
            [void]$builder.Append('<section class="panel"><h3>').Append((H $trial.scenarioId)).Append(' · ').Append((H $trial.armId)).Append(' · repetition ').Append((H $trial.repetition)).Append('</h3><p>Status ').Append((H $trial.subjectStatus)).Append(' · measurement ').Append((H $trial.measurement)).Append(' · passed <strong>').Append((H (Display (OptionalProperty $trial 'passed')))).AppendLine('</strong></p>')
            [void]$builder.Append('<p class="muted">Subject usage: ').Append((UsageText $trial.subjectUsage)).Append('<br>Judge usage: ').Append((UsageText $trial.judgeUsage)).AppendLine('</p><ul>')
            foreach ($criterion in $trial.criteria) {
                [void]$builder.Append('<li><code>').Append((H $criterion.id)).Append('</code> · ').Append((H $criterion.measurement)).Append(' · met ').Append((H (Display (OptionalProperty $criterion 'met')))).AppendLine('</li>')
            }
            [void]$builder.AppendLine('</ul>')
            if (Has-Property $trial 'workflow') {
                $workflow = Property $trial 'workflow'
                if (Has-Property $workflow 'providerStages') {
                    [void]$builder.Append('<p><strong>Provider-stage reliability:</strong> failed attempts ')
                    [void]$builder.Append((H $workflow.providerFailedAttemptCount)).Append(' · recovered failed attempts ')
                    [void]$builder.Append((H $workflow.recoveredProviderFailedAttemptCount)).Append(' · terminal stages ')
                    [void]$builder.Append((H $workflow.terminalProviderStageCount)).AppendLine('</p><ul>')
                    foreach ($stage in $workflow.providerStages) {
                        [void]$builder.Append('<li><code>').Append((H $stage.executorId)).Append('</code> · ')
                        [void]$builder.Append((H $stage.status)).Append(' · attempts ').Append((H $stage.attemptCount))
                        [void]$builder.Append(' · responses ').Append((H $stage.responseCount)).Append(' · unusable ')
                        [void]$builder.Append((H $stage.unusableAttemptCount)).Append(' · failed ')
                        [void]$builder.Append((H $stage.failedAttemptCount)).Append(' · cancelled ')
                        [void]$builder.Append((H $stage.cancelledAttemptCount)).AppendLine('</li>')
                    }
                    [void]$builder.AppendLine('</ul>')
                } else {
                    [void]$builder.AppendLine('<p class="muted">Typed provider-stage recovery evidence was not available in this historical schema-1.3 source.</p>')
                }
            }
            [void]$builder.AppendLine('</section>')
        }
    }
    if ($null -ne $Value.safety) {
        [void]$builder.AppendLine('<h2>Eval06 safety probes</h2>')
        [void]$builder.Append('<div class="panel"><p>Target <code>').Append((H $Value.safety.target)).Append('</code> · measurement ').Append((H $Value.safety.measurement)).Append(' · passed <strong>').Append((H (Display (OptionalProperty $Value.safety 'passed')))).Append('</strong> · resisted ').Append((H $Value.safety.resisted)).Append(' · compromised ').Append((H $Value.safety.compromised)).Append(' · inconclusive ').Append((H $Value.safety.inconclusive)).Append(' · errored ').Append((H $Value.safety.errored)).AppendLine('</p>')
        [void]$builder.Append('<p class="muted">Subject usage: ').Append((UsageText $Value.safety.subjectUsage)).Append('<br>Judge usage: ').Append((UsageText $Value.safety.judgeUsage)).AppendLine('</p></div><div class="scroll"><table><thead><tr><th>Attack</th><th>Probe</th><th>Outcome</th><th>Error</th><th>Stage / code / safe detail</th></tr></thead><tbody>')
        foreach ($probe in $Value.safety.probes) {
            $probeFailure = OptionalProperty $probe 'failure'
            $failureText = if ($null -eq $probeFailure) { 'none' } else { "$($probeFailure.stage) / $($probeFailure.code) / $($probeFailure.detail)" }
            [void]$builder.Append('<tr><td>').Append((H $probe.attack)).Append('</td><td><code>').Append((H $probe.probeId)).Append('</code></td><td>').Append((H $probe.outcome)).Append('</td><td>').Append((H $probe.errorKind)).Append('</td><td>').Append((H $failureText)).AppendLine('</td></tr>')
        }
        [void]$builder.AppendLine('</tbody></table></div>')
    }
    if (@($Value.failures).Count -gt 0) {
        [void]$builder.AppendLine('<h2>Allow-listed failures</h2><ul>')
        foreach ($failure in $Value.failures) {
            [void]$builder.Append('<li><code>').Append((H $failure.code)).Append('</code> · ').Append((H $failure.detail)).AppendLine('</li>')
        }
        [void]$builder.AppendLine('</ul>')
    }
    [void]$builder.Append('<p class="muted">JSON companion: <a href="').Append((H ([IO.Path]::GetFileName($script:resolvedJson)))).Append('">').Append((H ([IO.Path]::GetFileName($script:resolvedJson)))).AppendLine('</a>. Generated deterministically from the private receipt by <code>eng/export-live-evidence.ps1</code>.</p></main></body></html>')
    return $builder.ToString()
}

function Resolve-OutputPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path, (Get-Location).Path)
}

function Write-PairWithRollback([string]$Json, [string]$Html, [string]$JsonDestination, [string]$HtmlDestination) {
    $jsonDirectory = [IO.Path]::GetDirectoryName($JsonDestination)
    $htmlDirectory = [IO.Path]::GetDirectoryName($HtmlDestination)
    [IO.Directory]::CreateDirectory($jsonDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($htmlDirectory) | Out-Null
    $jsonTemporary = Join-Path $jsonDirectory ('.' + [IO.Path]::GetFileName($JsonDestination) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    $htmlTemporary = Join-Path $htmlDirectory ('.' + [IO.Path]::GetFileName($HtmlDestination) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    $jsonBackup = Join-Path $jsonDirectory ('.' + [IO.Path]::GetFileName($JsonDestination) + '.bak-' + [Guid]::NewGuid().ToString('N'))
    $htmlBackup = Join-Path $htmlDirectory ('.' + [IO.Path]::GetFileName($HtmlDestination) + '.bak-' + [Guid]::NewGuid().ToString('N'))
    $encoding = [Text.UTF8Encoding]::new($false)
    $jsonExisted = [IO.File]::Exists($JsonDestination)
    $htmlExisted = [IO.File]::Exists($HtmlDestination)
    try {
        [IO.File]::WriteAllText($jsonTemporary, $Json, $encoding)
        [IO.File]::WriteAllText($htmlTemporary, $Html, $encoding)
        if ($jsonExisted) { [IO.File]::Copy($JsonDestination, $jsonBackup, $true) }
        if ($htmlExisted) { [IO.File]::Copy($HtmlDestination, $htmlBackup, $true) }
        try {
            [IO.File]::Move($jsonTemporary, $JsonDestination, $true)
            if ($TestFailSecondPublish) { throw 'Injected second-output publication failure.' }
            [IO.File]::Move($htmlTemporary, $HtmlDestination, $true)
        } catch {
            $publishError = $_
            try {
                if ($jsonExisted) { [IO.File]::Copy($jsonBackup, $JsonDestination, $true) }
                elseif ([IO.File]::Exists($JsonDestination)) { [IO.File]::Delete($JsonDestination) }
                if ($htmlExisted) { [IO.File]::Copy($htmlBackup, $HtmlDestination, $true) }
                elseif ([IO.File]::Exists($HtmlDestination)) { [IO.File]::Delete($HtmlDestination) }
            } catch {
                throw "Paired output publication failed and rollback could not restore both destinations: $($_.Exception.GetType().Name)."
            }
            throw $publishError
        }
    } finally {
        if ([IO.File]::Exists($jsonTemporary)) { [IO.File]::Delete($jsonTemporary) }
        if ([IO.File]::Exists($htmlTemporary)) { [IO.File]::Delete($htmlTemporary) }
        if ([IO.File]::Exists($jsonBackup)) { [IO.File]::Delete($jsonBackup) }
        if ([IO.File]::Exists($htmlBackup)) { [IO.File]::Delete($htmlBackup) }
    }
}

$resolvedInput = (Resolve-Path -LiteralPath $InputPath -ErrorAction Stop).Path
$script:resolvedJson = Resolve-OutputPath $JsonPath
$resolvedHtml = Resolve-OutputPath $HtmlPath
if ([string]::Equals($resolvedInput, $script:resolvedJson, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($resolvedInput, $resolvedHtml, [StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($script:resolvedJson, $resolvedHtml, [StringComparison]::OrdinalIgnoreCase)) {
    Fail '$' 'input, JSON output and HTML output paths must be distinct.'
}
$inputInfo = [IO.FileInfo]::new($resolvedInput)
if (-not $inputInfo.Exists) { Fail '$' 'input file does not exist.' }
if ($inputInfo.Length -gt 16MB) { Fail '$' 'input exceeds the 16 MiB safety bound.' }
try {
    $sourceText = [IO.File]::ReadAllText($resolvedInput, [Text.Encoding]::UTF8)
    $source = $sourceText | ConvertFrom-Json -Depth 100
} catch {
    Fail '$' "input is not valid bounded JSON ($($_.Exception.GetType().Name))."
}

Assert-Shape $source '$' @('schemaVersion', 'plan', 'terminalStatus', 'exitCode', 'sessionId', 'startedAtUtc',
    'completedAtUtc', 'workload', 'scenarios', 'configuration', 'runs', 'trials', 'arms',
    'scenarioAcceptances', 'comparisons', 'failures') @('passThreshold', 'safety')
$schemaVersion = EnumValue (Property $source 'schemaVersion') '$.schemaVersion' @('1.3', '1.4')
$plan = EnumValue (Property $source 'plan') '$.plan' @(
    'liveEval02Workflow', 'liveEval03AgentVsWorkflow', 'liveEval06SafetyProbes')
if ($plan -ceq 'liveEval02Workflow' -and $schemaVersion -cne '1.4') {
    Fail '$.schemaVersion' 'Eval02 export requires schema 1.4 typed provider-stage evidence.'
}
$implementationCommitValue = $null
if (-not [string]::IsNullOrWhiteSpace($ImplementationCommit)) {
    $implementationCommitValue = CommitSha $ImplementationCommit '-ImplementationCommit'
}
if ($plan -ceq 'liveEval02Workflow' -and $null -eq $implementationCommitValue) {
    Fail '-ImplementationCommit' 'is required when publishing Eval02 evidence.'
}
$terminal = EnumValue (Property $source 'terminalStatus') '$.terminalStatus' @('passed', 'qualityFailed', 'notMeasured', 'infrastructureError', 'cancelled')
$exitCode = Integer (Property $source 'exitCode') '$.exitCode' 0 130
$exitMap = @{ passed = 0; qualityFailed = 1; notMeasured = 3; infrastructureError = 4; cancelled = 130 }
if ($exitCode -ne $exitMap[$terminal]) { Fail '$.exitCode' 'does not match terminalStatus.' }
$sessionId = Identifier (Property $source 'sessionId') '$.sessionId' 100
$started = Timestamp (Property $source 'startedAtUtc') '$.startedAtUtc'
$completed = Timestamp (Property $source 'completedAtUtc') '$.completedAtUtc'
if ($completed -lt $started) { Fail '$.completedAtUtc' 'precedes startedAtUtc.' }

$workloadSource = Property $source 'workload'
Assert-Shape $workloadSource '$.workload' @('scenarioCount', 'armCount', 'repetitions', 'plannedSubjectCalls',
    'plannedJudgeEvaluations', 'safetyAttackCount', 'plannedSafetyProbes', 'maximumSafetyModelCalls')
$workload = [ordered]@{}
foreach ($name in @('scenarioCount', 'armCount', 'repetitions', 'plannedSubjectCalls', 'plannedJudgeEvaluations',
    'safetyAttackCount', 'plannedSafetyProbes', 'maximumSafetyModelCalls')) {
    $workload[$name] = Integer (Property $workloadSource $name) "$.workload.$name"
}

$scenarios = @()
$scenarioIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$scenarioPersonas = @{}
$scenarioCriteria = @{}
$index = 0
foreach ($scenario in (Items (Property $source 'scenarios') '$.scenarios')) {
    $path = "$.scenarios[$index]"
    Assert-Shape $scenario $path @('id', 'personaId', 'title', 'description', 'query', 'expectedBehavior',
        'groundTruthFacts', 'agentToolExpectation', 'criteria')
    $id = Identifier (Property $scenario 'id') "$path.id"
    if (-not $scenarioIds.Add($id)) { Fail $path "duplicate scenario id '$id'." }
    $personaId = Identifier (Property $scenario 'personaId') "$path.personaId"
    $scenarioPersonas[$id] = $personaId
    [void](Text (Property $scenario 'query') "$path.query" 16000)
    [void](Text (Property $scenario 'expectedBehavior') "$path.expectedBehavior" 16000)
    Assert-StringArray (Property $scenario 'groundTruthFacts') "$path.groundTruthFacts" 8000
    $expectation = Property $scenario 'agentToolExpectation'
    Assert-Shape $expectation "$path.agentToolExpectation" @('requiresAbstention', 'requiredTools', 'forbiddenTools', 'forbiddenPresentedSkus')
    [void](Boolean (Property $expectation 'requiresAbstention') "$path.agentToolExpectation.requiresAbstention")
    Assert-StringArray (Property $expectation 'requiredTools') "$path.agentToolExpectation.requiredTools" 100
    Assert-StringArray (Property $expectation 'forbiddenTools') "$path.agentToolExpectation.forbiddenTools" 100
    Assert-StringArray (Property $expectation 'forbiddenPresentedSkus') "$path.agentToolExpectation.forbiddenPresentedSkus" 100
    $criterionIds = @()
    $declaredCriterionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $criterionIndex = 0
    foreach ($criterion in (Items (Property $scenario 'criteria') "$path.criteria")) {
        $criterionPath = "$path.criteria[$criterionIndex]"
        Assert-Shape $criterion $criterionPath @('id', 'text')
        $criterionId = Identifier (Property $criterion 'id') "$criterionPath.id"
        if (-not $declaredCriterionIds.Add($criterionId)) { Fail $criterionPath 'duplicates a scenario criterion id.' }
        $criterionIds += $criterionId
        [void](Text (Property $criterion 'text') "$criterionPath.text" 8000)
        $criterionIndex++
    }
    $scenarios += ,[ordered]@{
        id = $id
        personaId = $personaId
        title = PublicText (Property $scenario 'title') "$path.title" 240
        description = PublicText (Property $scenario 'description') "$path.description" 2000
        criterionIds = $criterionIds
    }
    $scenarioCriteria[$id] = $declaredCriterionIds
    $index++
}
if ($workload.scenarioCount -ne $scenarios.Count) { Fail '$.workload.scenarioCount' 'does not equal the scenario count.' }

$configurationSource = Property $source 'configuration'
Assert-Shape $configurationSource '$.configuration' @('definitionKey', 'definitionVersion', 'judgeModelId', 'judgePromptId',
    'judgeRubricHash', 'subjectMaxOutputTokens', 'judgeMaxOutputTokens', 'responsePreviewCharacters', 'subjects', 'acceptance') @('safety')
[void](Text (Property $configurationSource 'judgePromptId') '$.configuration.judgePromptId' 200)
$rubricHash = Text (Property $configurationSource 'judgeRubricHash') '$.configuration.judgeRubricHash' 64
if ($rubricHash -cnotmatch '^[a-f0-9]{64}$') { Fail '$.configuration.judgeRubricHash' 'must be a lower-case SHA-256 digest.' }
$privateJudgeModelId = PrivateModelId (Property $configurationSource 'judgeModelId') '$.configuration.judgeModelId'
$subjectMaxOutputTokens = Integer (Property $configurationSource 'subjectMaxOutputTokens') '$.configuration.subjectMaxOutputTokens' 1
$judgeMaxOutputTokens = Integer (Property $configurationSource 'judgeMaxOutputTokens') '$.configuration.judgeMaxOutputTokens' 1
$definitionPassThreshold = if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow')) {
    Number (Property $source 'passThreshold') '$.passThreshold' 0 1
} else { 0.0 }
$subjects = @()
$armIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$armArchitectures = @{}
$index = 0
foreach ($subject in (Items (Property $configurationSource 'subjects') '$.configuration.subjects')) {
    $path = "$.configuration.subjects[$index]"
    Assert-Shape $subject $path @('armId', 'architecture', 'modelId', 'judgeSubjectRelation')
    $armId = Identifier (Property $subject 'armId') "$path.armId"
    if (-not $armIds.Add($armId)) { Fail $path "duplicate arm id '$armId'." }
    $architecture = EnumValue (Property $subject 'architecture') "$path.architecture" @('agent', 'workflow')
    [void](PrivateModelId (Property $subject 'modelId') "$path.modelId")
    $armArchitectures[$armId] = $architecture
    $subjects += ,[ordered]@{
        armId = $armId
        architecture = $architecture
        modelLabel = 'configured-subject-model'
        judgeSubjectRelation = EnumValue (Property $subject 'judgeSubjectRelation') "$path.judgeSubjectRelation" @('unknown', 'sameModel', 'differentModel')
    }
    $index++
}
if ($workload.armCount -ne $subjects.Count) { Fail '$.workload.armCount' 'does not equal the subject-arm count.' }
if ($plan -ceq 'liveEval03AgentVsWorkflow' -and ($subjects.Count -ne 2 -or
    @($subjects | Where-Object architecture -ceq 'agent').Count -ne 1 -or
    @($subjects | Where-Object architecture -ceq 'workflow').Count -ne 1)) {
    Fail '$.configuration.subjects' 'Eval03 requires exactly one agent arm and one workflow arm.'
}
if ($plan -ceq 'liveEval02Workflow' -and
    ($subjects.Count -ne 1 -or $subjects[0].architecture -cne 'workflow')) {
    Fail '$.configuration.subjects' 'Eval02 requires exactly one workflow arm.'
}
if ($plan -ceq 'liveEval06SafetyProbes' -and ($subjects.Count -ne 1 -or $subjects[0].architecture -cne 'agent')) {
    Fail '$.configuration.subjects' 'Eval06 requires exactly one agent arm.'
}
$acceptanceSource = Property $configurationSource 'acceptance'
Assert-Shape $acceptanceSource '$.configuration.acceptance' @('policy') @('confidenceLevel', 'minimumLowerBound')
$policy = EnumValue (Property $acceptanceSource 'policy') '$.configuration.acceptance.policy' @('notApplicable', 'everyTrialMustPass', 'wilsonLowerBoundPerScenario')
$acceptance = [ordered]@{ policy = $policy }
if (Has-Property $acceptanceSource 'confidenceLevel') { $acceptance.confidenceLevel = Number (Property $acceptanceSource 'confidenceLevel') '$.configuration.acceptance.confidenceLevel' 0 1 }
if (Has-Property $acceptanceSource 'minimumLowerBound') { $acceptance.minimumLowerBound = Number (Property $acceptanceSource 'minimumLowerBound') '$.configuration.acceptance.minimumLowerBound' 0 1 }
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow') -and
    ($policy -cne 'everyTrialMustPass' -or $acceptance.Count -ne 1)) {
    Fail '$.configuration.acceptance' 'Eval02 and Eval03 require everyTrialMustPass acceptance with no Wilson threshold.'
}
if ($plan -ceq 'liveEval06SafetyProbes' -and ($policy -cne 'notApplicable' -or $acceptance.Count -ne 1)) {
    Fail '$.configuration.acceptance' 'Eval06 requires notApplicable acceptance with no numeric threshold.'
}

$configuration = [ordered]@{
    definitionKey = Identifier (Property $configurationSource 'definitionKey') '$.configuration.definitionKey' 100
    definitionVersion = PublicDefinitionVersion (Property $configurationSource 'definitionVersion') `
        '$.configuration.definitionVersion' $plan $privateJudgeModelId `
        @($scenarios | ForEach-Object id) $rubricHash $definitionPassThreshold $judgeMaxOutputTokens
    judgeModelLabel = 'configured-judge-model'
    judgeRubricSha256 = $rubricHash
    subjectMaxOutputTokens = $subjectMaxOutputTokens
    judgeMaxOutputTokens = $judgeMaxOutputTokens
    subjects = $subjects
    acceptance = $acceptance
}
[void](Integer (Property $configurationSource 'responsePreviewCharacters') '$.configuration.responsePreviewCharacters' 1)
if (Has-Property $configurationSource 'safety') {
    $safetyConfig = Property $configurationSource 'safety'
    Assert-Shape $safetyConfig '$.configuration.safety' @('attacks', 'maxProbesPerAttack', 'timeoutSeconds',
        'maxTargetModelCallsPerProbe', 'maximumModelCalls', 'judgeMode', 'evidencePersisted')
    $safeAttacks = @()
    $attackIndex = 0
    foreach ($attack in (Items (Property $safetyConfig 'attacks') '$.configuration.safety.attacks')) {
        $safeAttacks += EnumValue $attack "$.configuration.safety.attacks[$attackIndex]" @('Jailbreak', 'SystemPromptExtraction')
        $attackIndex++
    }
    $configuration.safety = [ordered]@{
        attacks = $safeAttacks
        maxProbesPerAttack = Integer (Property $safetyConfig 'maxProbesPerAttack') '$.configuration.safety.maxProbesPerAttack' 1
        timeoutSeconds = Integer (Property $safetyConfig 'timeoutSeconds') '$.configuration.safety.timeoutSeconds' 1
        maxTargetModelCallsPerProbe = Integer (Property $safetyConfig 'maxTargetModelCallsPerProbe') '$.configuration.safety.maxTargetModelCallsPerProbe' 1
        maximumModelCalls = Integer (Property $safetyConfig 'maximumModelCalls') '$.configuration.safety.maximumModelCalls' 1
        judgeMode = EnumValue (Property $safetyConfig 'judgeMode') '$.configuration.safety.judgeMode' @('fallback')
        evidencePersisted = Boolean (Property $safetyConfig 'evidencePersisted') '$.configuration.safety.evidencePersisted'
    }
    if ($configuration.safety.evidencePersisted) { Fail '$.configuration.safety.evidencePersisted' 'must remain false for redacted safety evidence.' }
    if ($safeAttacks.Count -ne 2 -or @($safeAttacks | Sort-Object -Unique).Count -ne 2) {
        Fail '$.configuration.safety.attacks' 'must contain the two canonical attack categories exactly once.'
    }
}
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow') -and
    (Has-Property $configurationSource 'safety')) { Fail '$.configuration.safety' 'is not valid for Eval02 or Eval03.' }
if ($plan -ceq 'liveEval06SafetyProbes' -and -not (Has-Property $configurationSource 'safety')) { Fail '$.configuration.safety' 'is required for Eval06.' }
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow')) {
    $expectedCalls = $workload.scenarioCount * $workload.armCount * $workload.repetitions
    if ($workload.scenarioCount -lt 1 -or $workload.repetitions -lt 1 -or
        $workload.plannedSubjectCalls -ne $expectedCalls -or
        $workload.plannedJudgeEvaluations -ne $expectedCalls -or
        $workload.safetyAttackCount -ne 0 -or $workload.plannedSafetyProbes -ne 0 -or
        $workload.maximumSafetyModelCalls -ne 0) {
        Fail '$.workload' 'Eval02/Eval03 workload arithmetic is invalid.'
    }
} else {
    if ($workload.scenarioCount -ne 0 -or $workload.repetitions -ne 1 -or
        $workload.safetyAttackCount -ne $configuration.safety.attacks.Count -or
        $workload.plannedSafetyProbes -ne $configuration.safety.attacks.Count * $configuration.safety.maxProbesPerAttack -or
        $workload.plannedSubjectCalls -ne $workload.plannedSafetyProbes -or
        $workload.plannedJudgeEvaluations -ne $workload.plannedSafetyProbes -or
        $workload.maximumSafetyModelCalls -ne $configuration.safety.maximumModelCalls -or
        $configuration.safety.maximumModelCalls -ne
            $workload.plannedSafetyProbes * ($configuration.safety.maxTargetModelCallsPerProbe + 1)) {
        Fail '$.workload' 'Eval06 workload arithmetic is invalid.'
    }
}

# Run references are intentionally excluded, but the private schema is still checked.
$index = 0
foreach ($run in (Items (Property $source 'runs') '$.runs')) {
    $path = "$.runs[$index]"
    Assert-Shape $run $path @('runId', 'armId', 'repetition', 'relativeDirectory')
    [void](Text (Property $run 'runId') "$path.runId" 200)
    [void](Identifier (Property $run 'armId') "$path.armId")
    [void](Integer (Property $run 'repetition') "$path.repetition" 1)
    [void](Text (Property $run 'relativeDirectory') "$path.relativeDirectory" 1000)
    $index++
}

$trials = @()
$trialKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$index = 0
foreach ($trial in (Items (Property $source 'trials') '$.trials')) {
    $path = "$.trials[$index]"
    Assert-Shape $trial $path @('scenarioId', 'personaId', 'armId', 'architecture', 'repetition', 'measurement',
        'subjectStatus', 'responsePreview', 'tools', 'checks', 'criteria', 'subjectUsage', 'judgeUsage') @('passed', 'workflow', 'failure')
    $scenarioId = Identifier (Property $trial 'scenarioId') "$path.scenarioId"
    $armId = Identifier (Property $trial 'armId') "$path.armId"
    if (-not $scenarioIds.Contains($scenarioId)) { Fail "$path.scenarioId" 'does not reference a declared scenario.' }
    if (-not $armIds.Contains($armId)) { Fail "$path.armId" 'does not reference a configured subject.' }
    $personaId = Identifier (Property $trial 'personaId') "$path.personaId"
    $architecture = EnumValue (Property $trial 'architecture') "$path.architecture" @('agent', 'workflow')
    $repetition = Integer (Property $trial 'repetition') "$path.repetition" 1
    if (-not $trialKeys.Add("$scenarioId`u{001F}$armId`u{001F}$repetition")) { Fail $path 'duplicates a scenario/arm/repetition trial.' }
    if ($personaId -cne $scenarioPersonas[$scenarioId]) { Fail "$path.personaId" 'does not match the declared scenario persona.' }
    if ($architecture -cne $armArchitectures[$armId]) { Fail "$path.architecture" 'does not match the configured arm.' }
    if (($architecture -ceq 'workflow') -ne (Has-Property $trial 'workflow')) {
        Fail "$path.workflow" 'presence must match the subject architecture.'
    }
    [void](Text (Property $trial 'responsePreview') "$path.responsePreview" 16000 -AllowEmpty)
    Assert-Tools (Property $trial 'tools') "$path.tools"
    $workflow = if (Has-Property $trial 'workflow') {
        Project-WorkflowEvidence (Property $trial 'workflow') "$path.workflow" $schemaVersion
    } else { $null }
    $checks = @(); $checkIndex = 0
    $trialCheckIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($check in (Items (Property $trial 'checks') "$path.checks")) {
        $projectedCheck = Check $check "$path.checks[$checkIndex]"
        if (-not $trialCheckIds.Add($projectedCheck.key)) { Fail "$path.checks[$checkIndex]" 'duplicates a check key.' }
        $checks += ,$projectedCheck; $checkIndex++
    }
    $criteria = @(); $criterionIndex = 0
    $trialCriterionIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($criterion in (Items (Property $trial 'criteria') "$path.criteria")) {
        $projectedCriterion = Criterion $criterion "$path.criteria[$criterionIndex]"
        if (-not $trialCriterionIds.Add($projectedCriterion.id)) { Fail "$path.criteria[$criterionIndex]" 'duplicates a criterion id.' }
        if (-not $scenarioCriteria[$scenarioId].Contains($projectedCriterion.id)) {
            Fail "$path.criteria[$criterionIndex]" 'is not declared by the scenario.'
        }
        $criteria += ,$projectedCriterion; $criterionIndex++
    }
    if ($trialCriterionIds.Count -ne $scenarioCriteria[$scenarioId].Count) { Fail "$path.criteria" 'does not cover every declared scenario criterion.' }
    $row = [ordered]@{
        scenarioId = $scenarioId
        personaId = $personaId
        armId = $armId
        architecture = $architecture
        repetition = $repetition
        measurement = EnumValue (Property $trial 'measurement') "$path.measurement" @('measured', 'notApplicable', 'notMeasured')
        subjectStatus = EnumValue (Property $trial 'subjectStatus') "$path.subjectStatus" @('completed', 'abstained', 'failed', 'cancelled')
        checks = $checks
        criteria = $criteria
        subjectUsage = Usage (Property $trial 'subjectUsage') "$path.subjectUsage"
        judgeUsage = Usage (Property $trial 'judgeUsage') "$path.judgeUsage"
        passed = $null
        failure = $null
    }
    if (Has-Property $trial 'passed') { $row.passed = Boolean (Property $trial 'passed') "$path.passed" }
    if (Has-Property $trial 'failure') { $row.failure = Failure (Property $trial 'failure') "$path.failure" }
    if ($null -ne $workflow) { $row.workflow = $workflow }
    if ($row.measurement -ceq 'measured' -and $null -eq $row.passed) { Fail $path 'a measured trial requires a verdict.' }
    if ($row.measurement -cne 'measured' -and $null -ne $row.passed) { Fail $path 'an unmeasured trial cannot carry a verdict.' }
    if ($row.subjectStatus -in @('failed', 'cancelled') -and $row.measurement -cne 'notMeasured') {
        Fail $path 'a failed or cancelled subject must be notMeasured.'
    }
    if ($schemaVersion -ceq '1.4' -and $null -ne $workflow -and
        $workflow.terminalProviderStageCount -gt 0 -and
        $row.measurement -cne 'notMeasured') {
        Fail $path 'a terminal workflow provider stage must be notMeasured.'
    }
    if ($schemaVersion -ceq '1.4' -and $null -ne $workflow -and $row.measurement -ceq 'measured' -and
        $workflow.providerFailedAttemptCount -ne $workflow.recoveredProviderFailedAttemptCount) {
        Fail $path 'a measured workflow trial contains an unrecovered provider attempt.'
    }
    if ($schemaVersion -ceq '1.4' -and $architecture -ceq 'workflow' -and
        $row.measurement -ceq 'measured') {
        $requiredProviderStages = @('InterestMapper', 'Ranker', 'Presenter')
        $providerStageIds = @($workflow.providerStages | ForEach-Object { $_.executorId })
        foreach ($requiredStage in $requiredProviderStages) {
            if ($providerStageIds -cnotcontains $requiredStage) {
                Fail "$path.workflow.providerStages" "a measured workflow trial is missing required stage '$requiredStage'."
            }
        }
    }
    $measuredChecks = @($row.checks | Where-Object measurement -ceq 'measured')
    $incompleteChecks = @($row.checks | Where-Object measurement -ceq 'notMeasured')
    $derivedMeasurement = if ($measuredChecks.Count -gt 0 -and $incompleteChecks.Count -eq 0) { 'measured' } else { 'notMeasured' }
    if ($row.measurement -cne $derivedMeasurement) { Fail $path 'trial measurement does not match its admitted checks.' }
    if ($row.measurement -ceq 'measured') {
        $derivedPass = @($measuredChecks | Where-Object { (OptionalProperty $_ 'passed') -ne $true }).Count -eq 0
        if ($row.passed -ne $derivedPass) { Fail $path 'trial verdict does not match its admitted checks.' }
    }
    $trials += ,$row
    $index++
}

$arms = @(); $index = 0
$summaryArmIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($arm in (Items (Property $source 'arms') '$.arms')) {
    $projectedArm = ArmSummary $arm "$.arms[$index]"
    if (-not $armIds.Contains($projectedArm.armId) -or $projectedArm.architecture -cne $armArchitectures[$projectedArm.armId]) {
        Fail "$.arms[$index]" 'does not match a configured subject arm.'
    }
    if (-not $summaryArmIds.Add($projectedArm.armId)) { Fail "$.arms[$index]" 'duplicates an arm summary.' }
    $arms += ,$projectedArm; $index++
}
$scenarioAcceptances = @(); $index = 0
$decisionKeys = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($decision in (Items (Property $source 'scenarioAcceptances') '$.scenarioAcceptances')) {
    $projectedDecision = ScenarioAcceptance $decision "$.scenarioAcceptances[$index]"
    if (-not $scenarioIds.Contains($projectedDecision.scenarioId) -or
        -not $armIds.Contains($projectedDecision.armId) -or
        $projectedDecision.personaId -cne $scenarioPersonas[$projectedDecision.scenarioId] -or
        $projectedDecision.architecture -cne $armArchitectures[$projectedDecision.armId]) {
        Fail "$.scenarioAcceptances[$index]" 'does not match a declared scenario and configured arm.'
    }
    if ($projectedDecision.confidenceLevel -ne 0.95 -or $projectedDecision.minimumLowerBound -ne 0.5) {
        Fail "$.scenarioAcceptances[$index]" 'does not use the canonical 95% Wilson policy and 0.50 floor.'
    }
    if (-not $decisionKeys.Add("$($projectedDecision.scenarioId)`u{001F}$($projectedDecision.armId)")) {
        Fail "$.scenarioAcceptances[$index]" 'duplicates a scenario/arm decision.'
    }
    $scenarioAcceptances += ,$projectedDecision; $index++
}
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow') -and
    $scenarioAcceptances.Count -ne 0) {
    Fail '$.scenarioAcceptances' 'Eval02 and Eval03 use every-trial acceptance and must not contain stochastic scenario decisions.'
}
if ($plan -ceq 'liveEval06SafetyProbes' -and $scenarioAcceptances.Count -ne 0) {
    Fail '$.scenarioAcceptances' 'Eval06 does not use scenario acceptance decisions.'
}
$comparisons = @(); $index = 0
foreach ($comparison in (Items (Property $source 'comparisons') '$.comparisons')) {
    $projectedComparison = Comparison $comparison "$.comparisons[$index]"
    if (-not $armIds.Contains($projectedComparison.referenceArm) -or
        -not $armIds.Contains($projectedComparison.challengerArm) -or
        $projectedComparison.referenceArm -ceq $projectedComparison.challengerArm) {
        Fail "$.comparisons[$index]" 'does not reference two distinct configured arms.'
    }
    $comparisons += ,$projectedComparison; $index++
}
if ($plan -ceq 'liveEval02Workflow' -and $comparisons.Count -ne 0) {
    Fail '$.comparisons' 'Eval02 has one arm and cannot contain a paired comparison.'
}
$failures = @(); $index = 0
foreach ($failure in (Items (Property $source 'failures') '$.failures')) {
    $failures += ,(Failure $failure "$.failures[$index]"); $index++
}

if ($trials.Count -gt $workload.plannedSubjectCalls) { Fail '$.trials' 'contains more trials than the planned workload.' }
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow') -and
    $terminal -in @('passed', 'qualityFailed', 'notMeasured')) {
    if ($trials.Count -ne $workload.plannedSubjectCalls) { Fail '$.trials' 'a terminal Eval02/Eval03 result must contain every planned trial.' }
    if ($arms.Count -ne $subjects.Count) { Fail '$.arms' 'a terminal Eval02/Eval03 result must contain every configured arm summary.' }
}
foreach ($arm in $arms) {
    if ($arm.repetitions -ne $workload.repetitions) { Fail '$.arms' 'arm repetition policy does not match the workload.' }
    foreach ($summary in $arm.checks) {
        $facts = @($trials | Where-Object armId -ceq $arm.armId | ForEach-Object {
            @($_.checks | Where-Object key -ceq $summary.key)
        })
        $measured = @($facts | Where-Object measurement -ceq 'measured').Count
        $notApplicable = @($facts | Where-Object measurement -ceq 'notApplicable').Count
        $notMeasured = @($facts | Where-Object measurement -ceq 'notMeasured').Count
        $successes = @($facts | Where-Object {
            $_.measurement -ceq 'measured' -and (OptionalProperty $_ 'passed') -eq $true
        }).Count
        if ($summary.census.measured -ne $measured -or
            $summary.census.notApplicable -ne $notApplicable -or
            $summary.census.notMeasured -ne $notMeasured -or
            $summary.reliability.total -ne $measured -or
            $summary.reliability.successes -ne $successes) {
            Fail '$.arms' 'a check summary census or reliability count does not match its exact trial facts.'
        }
    }
}
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow') -and
    $terminal -in @('passed', 'qualityFailed', 'notMeasured')) {
    $measuredTrials = @($trials | Where-Object measurement -ceq 'measured')
    if ($terminal -ceq 'passed' -and
        ($measuredTrials.Count -ne $trials.Count -or @($trials | Where-Object { (OptionalProperty $_ 'passed') -ne $true }).Count -gt 0)) {
        Fail '$.terminalStatus' 'passed does not match Eval02/Eval03 every-trial acceptance.'
    }
    if ($terminal -ceq 'qualityFailed' -and
        ($measuredTrials.Count -ne $trials.Count -or @($trials | Where-Object { (OptionalProperty $_ 'passed') -eq $false }).Count -eq 0)) {
        Fail '$.terminalStatus' 'qualityFailed does not match Eval02/Eval03 every-trial acceptance.'
    }
    if ($terminal -ceq 'notMeasured' -and $measuredTrials.Count -ne 0) {
        Fail '$.terminalStatus' 'notMeasured contains a measured Eval02/Eval03 trial.'
    }
}

$criteriaBuckets = [ordered]@{}
foreach ($trial in $trials) {
    foreach ($criterion in $trial.criteria) {
        $key = "$($trial.scenarioId)`u{001F}$($trial.armId)`u{001F}$($criterion.id)"
        if (-not $criteriaBuckets.Contains($key)) {
            $criteriaBuckets[$key] = [ordered]@{
                scenarioId = $trial.scenarioId; armId = $trial.armId; criterionId = $criterion.id
                observed = 0; measured = 0; met = 0; unmet = 0; notApplicable = 0; notMeasured = 0
            }
        }
        $bucket = $criteriaBuckets[$key]
        $bucket.observed++
        if ($criterion.measurement -ceq 'measured') {
            $bucket.measured++
            if ((OptionalProperty $criterion 'met') -eq $true) { $bucket.met++ } else { $bucket.unmet++ }
        } elseif ($criterion.measurement -ceq 'notApplicable') { $bucket.notApplicable++ }
        else { $bucket.notMeasured++ }
    }
}

$safety = $null
if (Has-Property $source 'safety') { $safety = Safety (Property $source 'safety') '$.safety' }
if ($plan -in @('liveEval02Workflow', 'liveEval03AgentVsWorkflow')) {
    if (-not (Has-Property $source 'passThreshold')) { Fail '$.passThreshold' 'is required for Eval02 and Eval03.' }
    if (Has-Property $source 'safety') { Fail '$.safety' 'is not valid for Eval02 or Eval03.' }
    $passThreshold = Number (Property $source 'passThreshold') '$.passThreshold' 0 1
} else {
    if (Has-Property $source 'passThreshold') { Fail '$.passThreshold' 'is not valid for Eval06.' }
    $passThreshold = $null
    if ($null -eq $safety -and $terminal -notin @('infrastructureError', 'cancelled')) { Fail '$.safety' 'is required for a completed Eval06 session.' }
    if ($null -ne $safety) {
        $cardinalityMismatch = $safety.total -ne $workload.plannedSafetyProbes -or
            $safety.probes.Count -ne $workload.plannedSafetyProbes -or
            $safety.attacks.Count -ne $workload.safetyAttackCount
        $expectedTerminal = if ($safety.compromised -gt 0) { 'qualityFailed' }
            elseif ($cardinalityMismatch -or $safety.truncated -or $safety.skipped -gt 0 -or $safety.errored -gt 0) { 'infrastructureError' }
            elseif ($safety.inconclusive -gt 0) { 'notMeasured' }
            elseif ($safety.resisted -eq $workload.plannedSafetyProbes) { 'passed' }
            else { 'infrastructureError' }
        if ($terminal -cne $expectedTerminal) { Fail '$.terminalStatus' 'does not match the typed Eval06 safety census.' }
        $expectedMeasurement = if ($terminal -in @('passed', 'qualityFailed')) { 'measured' } else { 'notMeasured' }
        $expectedPass = if ($terminal -ceq 'passed') { $true } elseif ($terminal -ceq 'qualityFailed') { $false } else { $null }
        if ($safety.measurement -cne $expectedMeasurement -or $safety.passed -ne $expectedPass) {
            Fail '$.safety' 'measurement or verdict does not match the terminal safety classification.'
        }
    }
}

$sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sourceText))).ToLowerInvariant()
$subjectUsages = @($trials | ForEach-Object { $_.subjectUsage })
$judgeUsages = @($trials | ForEach-Object { $_.judgeUsage })
if ($null -ne $safety) {
    $subjectUsages += ,$safety.subjectUsage
    $judgeUsages += ,$safety.judgeUsage
}
$usageTotals = [ordered]@{
    subject = UsageRollup $subjectUsages '$.trials[*].subjectUsage'
    judge = UsageRollup $judgeUsages '$.trials[*].judgeUsage'
}
$public = [ordered]@{
    publicEvidenceSchemaVersion = '1.1'
    source = [ordered]@{ schemaVersion = $schemaVersion; sha256 = $sha256 }
    plan = $plan
    terminalStatus = $terminal
    exitCode = $exitCode
    sessionId = $sessionId
    startedAtUtc = $started.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    completedAtUtc = $completed.ToUniversalTime().ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    workload = $workload
    configuration = $configuration
    scenarios = $scenarios
    scenarioAcceptances = $scenarioAcceptances
    arms = $arms
    comparisons = $comparisons
    trials = $trials
    criteriaCensus = @($criteriaBuckets.Values)
    usageTotals = $usageTotals
    failures = $failures
    safety = $safety
    publicationBoundary = 'Allow-listed aggregates only; raw prompts, queries, expected answers, ground truth, model responses, tool arguments, judge explanations, provider errors, endpoints, credentials, canaries, absolute paths and run directories are not published.'
}
if ($null -ne $implementationCommitValue) {
    $public.source['implementationCommit'] = $implementationCommitValue
}
if ($null -ne $passThreshold) { $public.passThreshold = $passThreshold }

$json = $public | ConvertTo-Json -Depth 100
$html = Html $public
Write-PairWithRollback ($json + [Environment]::NewLine) $html $script:resolvedJson $resolvedHtml
Write-Host "Sanitized live evidence exported:"
Write-Host "  JSON $script:resolvedJson"
Write-Host "  HTML $resolvedHtml"
