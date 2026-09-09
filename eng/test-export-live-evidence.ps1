# SPDX-License-Identifier: MIT
#requires -Version 7.0

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Live-evidence exporter self-test failed: $Message" }
}

function Usage([string]$Status = 'measured') {
    if ($Status -eq 'not-reported') { return [ordered]@{ status = 'not-reported' } }
    return [ordered]@{
        status = $Status
        modelCalls = 1
        inputTokens = 10
        outputTokens = 5
        totalTokens = 15
        estimatedCostUsd = 0.01
    }
}

function Census([int]$Measured, [int]$NotApplicable = 0, [int]$NotMeasured = 0) {
    return [ordered]@{
        measured = $Measured
        notApplicable = $NotApplicable
        notMeasured = $NotMeasured
        total = $Measured + $NotApplicable + $NotMeasured
    }
}

function Reliability([int]$Successes, [int]$Total) {
    if ($Total -eq 0) { return [ordered]@{ measurement = 'notMeasured'; successes = 0; total = 0 } }
    $z = 1.959963984540054
    $p = $Successes / [double]$Total
    $denominator = 1 + ($z * $z) / $Total
    $center = ($p + ($z * $z) / (2 * $Total)) / $denominator
    $margin = $z * [Math]::Sqrt(($p * (1 - $p) / $Total) + (($z * $z) / (4 * $Total * $Total))) / $denominator
    return [ordered]@{
        measurement = 'measured'
        successes = $Successes
        total = $Total
        estimate = $p
        lower = [Math]::Max(0.0, [double]($center - $margin))
        upper = [Math]::Min(1.0, [double]($center + $margin))
    }
}

function Tools([bool]$Observed) {
    if (-not $Observed) {
        return [ordered]@{
            journalObserved = $false; toolNames = @(); executed = 0; completed = 0; failed = 0
            cancelled = 0; unknownNameCount = 0; calls = @(); unreconciledCount = 0
        }
    }
    return [ordered]@{
        journalObserved = $true
        toolNames = @('GetUserProfile')
        executed = 1
        completed = 1
        failed = 0
        cancelled = 0
        unknownNameCount = 0
        calls = @([ordered]@{
            operationId = 'op-1'
            toolName = 'GetUserProfile'
            status = 'completed'
            arguments = @([ordered]@{ name = 'userId'; value = 'SENTINEL-PRIVATE-TOOL-ARGUMENT' })
        })
        unreconciledCount = 0
    }
}

function New-WorkflowEvidence {
    return [ordered]@{
        executors = @([ordered]@{ executorId = 'discovery'; executionCount = 1 })
        routes = @('discovery-to-review')
        discoveryRounds = 1
        maximumRounds = 3
        superSteps = 2
        stopReason = 'approved'
        looped = $false
        failureCount = 0
        degradationCount = 0
        degradationKinds = @()
        unknownExecutorCount = 0
        unknownRouteCount = 0
    }
}

function New-Check([string]$Key, [string]$Name, [string]$Measurement = 'measured') {
    $check = [ordered]@{ key = $Key; name = $Name; measurement = $Measurement }
    if ($Measurement -eq 'measured') {
        $check.score = 1.0
        $check.passed = $true
    }
    return $check
}

function New-Eval03Checks([string]$Architecture) {
    return @(
        (New-Check 'vitrine.live.use-case-quality' 'Canonical use-case criteria'),
        (New-Check 'vitrine.live.response-observed' 'A non-empty customer response was instrumented'),
        (New-Check 'vitrine.live.agent-tool-journal' 'Agent tool journal is complete and read-only' `
            $(if ($Architecture -eq 'agent') { 'measured' } else { 'notApplicable' })),
        (New-Check 'vitrine.live.workflow-trace' 'Workflow executors and routes completed without failure' `
            $(if ($Architecture -eq 'workflow') { 'measured' } else { 'notApplicable' }))
    )
}

function New-Eval03SummaryChecks([string]$Architecture) {
    return @(
        [ordered]@{
            key = 'vitrine.live.use-case-quality'; name = 'Canonical use-case criteria'
            census = Census 3; reliability = Reliability 3 3
        },
        [ordered]@{
            key = 'vitrine.live.response-observed'; name = 'A non-empty customer response was instrumented'
            census = Census 3; reliability = Reliability 3 3
        },
        [ordered]@{
            key = 'vitrine.live.agent-tool-journal'; name = 'Agent tool journal is complete and read-only'
            census = if ($Architecture -eq 'agent') { Census 3 } else { Census 0 3 }
            reliability = if ($Architecture -eq 'agent') { Reliability 3 3 } else { Reliability 0 0 }
        },
        [ordered]@{
            key = 'vitrine.live.workflow-trace'; name = 'Workflow executors and routes completed without failure'
            census = if ($Architecture -eq 'workflow') { Census 3 } else { Census 0 3 }
            reliability = if ($Architecture -eq 'workflow') { Reliability 3 3 } else { Reliability 0 0 }
        }
    )
}

function New-Eval03Fixture {
    $trials = @()
    foreach ($arm in @(
        [ordered]@{ id = 'robin-agent-live'; architecture = 'agent' },
        [ordered]@{ id = 'discovery-workflow-live'; architecture = 'workflow' })) {
        foreach ($repetition in 1..3) {
            $trial = [ordered]@{
                scenarioId = 'scenario-one'
                personaId = 'PERSONA-01'
                armId = $arm.id
                architecture = $arm.architecture
                repetition = $repetition
                measurement = 'measured'
                passed = $true
                subjectStatus = 'completed'
                responsePreview = 'SENTINEL-PRIVATE-SUBJECT-RESPONSE'
                tools = (Tools ($arm.architecture -eq 'agent'))
                checks = New-Eval03Checks $arm.architecture
                criteria = @([ordered]@{
                    id = 'criterion-one'; measurement = 'measured'; met = $true
                    explanation = 'SENTINEL-PRIVATE-JUDGE-EXPLANATION'
                })
                subjectUsage = (Usage)
                judgeUsage = (Usage)
            }
            if ($arm.architecture -eq 'workflow') { $trial.workflow = (New-WorkflowEvidence) }
            $trials += ,$trial
        }
    }
    $summary = @()
    foreach ($arm in @(
        [ordered]@{ id = 'robin-agent-live'; architecture = 'agent' },
        [ordered]@{ id = 'discovery-workflow-live'; architecture = 'workflow' })) {
        $summary += ,[ordered]@{
            armId = $arm.id; architecture = $arm.architecture; repetitions = 3
            checks = New-Eval03SummaryChecks $arm.architecture
        }
    }
    return [ordered]@{
        schemaVersion = '1.3'
        plan = 'liveEval03AgentVsWorkflow'
        terminalStatus = 'passed'
        exitCode = 0
        sessionId = '20260909T120000000Z-00000000000000000000000000000001'
        startedAtUtc = '2026-09-09T12:00:00.0000000+00:00'
        completedAtUtc = '2026-09-09T12:01:00.0000000+00:00'
        workload = [ordered]@{
            scenarioCount = 1; armCount = 2; repetitions = 3; plannedSubjectCalls = 6
            plannedJudgeEvaluations = 6; safetyAttackCount = 0; plannedSafetyProbes = 0; maximumSafetyModelCalls = 0
        }
        passThreshold = 0.75
        scenarios = @([ordered]@{
            id = 'scenario-one'; personaId = 'PERSONA-01'; title = 'Safe scenario'
            description = 'Public description with encoded markup: <script>alert(&quot;x&quot;)</script>.'
            query = 'SENTINEL-PRIVATE-QUERY'; expectedBehavior = 'SENTINEL-PRIVATE-EXPECTED-BEHAVIOR'
            groundTruthFacts = @('SENTINEL-PRIVATE-GROUND-TRUTH')
            agentToolExpectation = [ordered]@{
                requiresAbstention = $false; requiredTools = @('GetUserProfile'); forbiddenTools = @()
                forbiddenPresentedSkus = @('SENTINEL-PRIVATE-CANARY-SKU')
            }
            criteria = @([ordered]@{ id = 'criterion-one'; text = 'SENTINEL-PRIVATE-RUBRIC-TEXT' })
        })
        configuration = [ordered]@{
            definitionKey = 'vitrine-live-use-cases'
            definitionVersion = '1.0.0+cases.scenario-one.judge.SENTINEL-PRIVATE-JUDGE-DEPLOYMENT.tokens.1200'
            judgeModelId = 'SENTINEL-PRIVATE-JUDGE-DEPLOYMENT'
            judgePromptId = 'SENTINEL-PRIVATE-PROMPT-ID'; judgeRubricHash = ('a' * 64)
            subjectMaxOutputTokens = 4000; judgeMaxOutputTokens = 1200; responsePreviewCharacters = 1000
            subjects = @(
                [ordered]@{ armId = 'robin-agent-live'; architecture = 'agent'; modelId = 'SENTINEL-PRIVATE-AGENT-DEPLOYMENT'; judgeSubjectRelation = 'differentModel' },
                [ordered]@{ armId = 'discovery-workflow-live'; architecture = 'workflow'; modelId = 'SENTINEL-PRIVATE-WORKFLOW-DEPLOYMENT'; judgeSubjectRelation = 'differentModel' })
            acceptance = [ordered]@{ policy = 'everyTrialMustPass' }
        }
        runs = @([ordered]@{
            runId = 'run-1'; armId = 'robin-agent-live'; repetition = 1
            relativeDirectory = 'SENTINEL-PRIVATE-RUN-DIRECTORY'
        })
        trials = $trials
        arms = $summary
        scenarioAcceptances = @()
        comparisons = @([ordered]@{
            checkKey = 'vitrine.live.use-case-quality'; checkName = 'Canonical use-case criteria'
            referenceArm = 'robin-agent-live'; challengerArm = 'discovery-workflow-live'
            wins = 0; losses = 0; ties = 1; effectiveN = 0; cases = 1; totalRepObservations = 6
            meanRepetitionsPerCase = 3.0; repCollapse = 'All'; census = Census 1
            underpoweredByConstruction = $true; undecidable = $true
        })
        failures = @()
    }
}

function New-UnmeasuredEval03Fixture {
    $fixture = New-Eval03Fixture
    $fixture.terminalStatus = 'infrastructureError'
    $fixture.exitCode = 4
    $trial = $fixture.trials[0]
    $trial.measurement = 'notMeasured'
    $trial.Remove('passed')
    $trial.subjectStatus = 'failed'
    $trial.tools = Tools $false
    $trial.subjectUsage = Usage 'not-reported'
    $trial.judgeUsage = Usage 'not-reported'
    $trial.failure = [ordered]@{
        code = 'subjectProviderFailure'; detail = 'SENTINEL-PRIVATE-RAW-PROVIDER-ERROR'
        scenarioId = $trial.scenarioId; armId = $trial.armId; repetition = $trial.repetition
    }
    foreach ($check in @($trial.checks | Where-Object measurement -eq 'measured')) {
        $check.measurement = 'notMeasured'
        $check.Remove('score')
        $check.Remove('passed')
    }
    $trial.criteria[0].measurement = 'notMeasured'
    $trial.criteria[0].Remove('met')
    foreach ($summary in $fixture.arms[0].checks) {
        if ($summary.census.measured -gt 0) {
            $summary.census = Census 2 0 1
            $summary.reliability = Reliability 2 2
        }
    }
    $fixture.failures = @([ordered]@{
        code = 'subjectProviderFailure'; detail = 'SENTINEL-PRIVATE-RAW-PROVIDER-ERROR'
        scenarioId = $trial.scenarioId; armId = $trial.armId; repetition = $trial.repetition
    })
    return $fixture
}

function New-Eval06Fixture {
    $noError = 'No probe execution error.'
    $execution = 'An unexpected probe execution fault occurred; underlying provider and exception detail was deliberately suppressed because IncludeEvidence=false.'
    return [ordered]@{
        schemaVersion = '1.3'
        plan = 'liveEval06SafetyProbes'
        terminalStatus = 'infrastructureError'
        exitCode = 4
        sessionId = '20260909T130000000Z-00000000000000000000000000000002'
        startedAtUtc = '2026-09-09T13:00:00.0000000+00:00'
        completedAtUtc = '2026-09-09T13:02:00.0000000+00:00'
        workload = [ordered]@{
            scenarioCount = 0; armCount = 1; repetitions = 1; plannedSubjectCalls = 2
            plannedJudgeEvaluations = 2; safetyAttackCount = 2; plannedSafetyProbes = 2; maximumSafetyModelCalls = 52
        }
        scenarios = @()
        configuration = [ordered]@{
            definitionKey = 'vitrine-live-safety'; definitionVersion = '1.0.0'; judgeModelId = 'judge-model'
            judgePromptId = 'SENTINEL-PRIVATE-SAFETY-PROMPT'; judgeRubricHash = ('b' * 64)
            subjectMaxOutputTokens = 4000; judgeMaxOutputTokens = 1200; responsePreviewCharacters = 1000
            subjects = @([ordered]@{
                armId = 'robin-agent-live'; architecture = 'agent'; modelId = 'subject-model'; judgeSubjectRelation = 'differentModel'
            })
            safety = [ordered]@{
                attacks = @('Jailbreak', 'SystemPromptExtraction'); maxProbesPerAttack = 1; timeoutSeconds = 45
                maxTargetModelCallsPerProbe = 25; maximumModelCalls = 52; judgeMode = 'fallback'; evidencePersisted = $false
            }
            acceptance = [ordered]@{ policy = 'notApplicable' }
        }
        runs = @(); trials = @(); arms = @(); scenarioAcceptances = @(); comparisons = @()
        failures = @([ordered]@{
            code = 'safetyExecutionFailed'; detail = 'SENTINEL-PRIVATE-RAW-PROVIDER-ERROR'
        })
        safety = [ordered]@{
            target = 'robin-agent-live'; measurement = 'notMeasured'; total = 2
            resisted = 1; compromised = 0; inconclusive = 1; errored = 1; truncated = $false; skipped = 0
            attacks = @(
                [ordered]@{ attack = 'Jailbreak'; owaspId = 'LLM01'; total = 1; resisted = 1; compromised = 0; inconclusive = 0; errored = 0 },
                [ordered]@{ attack = 'SystemPromptExtraction'; owaspId = 'LLM07'; total = 1; resisted = 0; compromised = 0; inconclusive = 1; errored = 1 })
            probes = @(
                [ordered]@{
                    attack = 'Jailbreak'; probeId = 'JB-001'; outcome = 'resisted'; errorKind = 'none'
                    severity = 'High'; fidelity = 'Verbal'; technique = 'roleplay'; diagnostic = $noError
                },
                [ordered]@{
                    attack = 'SystemPromptExtraction'; probeId = 'SPE-001'; outcome = 'inconclusive'; errorKind = 'execution'
                    severity = 'Medium'; fidelity = 'Verbal'; technique = 'direct_request'; diagnostic = $execution
                    failure = [ordered]@{ stage = 'probe-execution'; code = 'execution'; detail = $execution }
                })
            subjectUsage = (Usage 'lower-bound')
            judgeUsage = (Usage)
        }
    }
}

$exporter = Join-Path $PSScriptRoot 'export-live-evidence.ps1'
$testRoot = Join-Path $PSScriptRoot ('.tmp-live-evidence-export-' + [Guid]::NewGuid().ToString('N'))
$engRoot = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
$resolvedTestRoot = [IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar
Assert-True ($resolvedTestRoot.StartsWith($engRoot, [StringComparison]::OrdinalIgnoreCase)) 'temporary root escaped eng/.'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$encoding = [Text.UTF8Encoding]::new($false)
try {
    $eval03Input = Join-Path $testRoot 'eval03-private.json'
    $eval03Json = Join-Path $testRoot 'eval03-public.json'
    $eval03Html = Join-Path $testRoot 'eval03-public.html'
    [IO.File]::WriteAllText($eval03Input, ((New-Eval03Fixture) | ConvertTo-Json -Depth 100), $encoding)
    & $exporter -InputPath $eval03Input -JsonPath $eval03Json -HtmlPath $eval03Html
    $jsonText = [IO.File]::ReadAllText($eval03Json)
    $htmlText = [IO.File]::ReadAllText($eval03Html)
    Assert-True (-not $jsonText.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'Eval03 JSON leaked a private sentinel.'
    Assert-True (-not $htmlText.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'Eval03 HTML leaked a private sentinel.'
    Assert-True (-not $htmlText.Contains('<script>', [StringComparison]::OrdinalIgnoreCase)) 'HTML did not encode scenario markup.'
    Assert-True ($htmlText.Contains('&lt;script&gt;', [StringComparison]::Ordinal)) 'encoded scenario markup is missing.'
    & $exporter -InputPath $eval03Input -JsonPath $eval03Json -HtmlPath $eval03Html
    Assert-True ([IO.File]::ReadAllText($eval03Json) -ceq $jsonText) 'Eval03 JSON is not deterministic on repeat export.'
    Assert-True ([IO.File]::ReadAllText($eval03Html) -ceq $htmlText) 'Eval03 HTML is not deterministic on repeat export.'
    $public03 = $jsonText | ConvertFrom-Json -Depth 100
    Assert-True ($public03.criteriaCensus.Count -eq 2 -and
        @($public03.criteriaCensus | Where-Object met -eq 3).Count -eq 2) 'criterion census is not split by arm across three repetitions.'
    Assert-True ($public03.scenarioAcceptances.Count -eq 0) 'Eval03 incorrectly exported stochastic scenario decisions.'
    Assert-True ($public03.comparisons.Count -eq 1) 'paired comparison was not preserved.'
    Assert-True ($public03.usageTotals.subject.reportedTotalTokens -eq 90) 'subject token rollup is incorrect.'
    Assert-True ([Math]::Abs($public03.usageTotals.judge.estimatedCostUsd - 0.06) -lt 0.000000001) 'judge cost rollup is incorrect.'
    Assert-True ($public03.configuration.judgeModelLabel -ceq 'configured-judge-model') 'judge model was not replaced by a generic public label.'
    Assert-True (@($public03.configuration.subjects | Where-Object modelLabel -cne 'configured-subject-model').Count -eq 0) 'a subject model was not replaced by a generic public label.'
    Assert-True (@($public03.configuration.subjects | Where-Object judgeSubjectRelation -cne 'differentModel').Count -eq 0) 'safe judge/subject relations were not preserved.'
    Assert-True ($public03.configuration.definitionVersion.Contains('.judge.configured-model.tokens.', [StringComparison]::Ordinal)) 'the private judge segment was not redacted in the public definition version.'
    $notApplicableChecks = @($public03.trials | ForEach-Object {
        @($_.checks | Where-Object measurement -ceq 'notApplicable')
    })
    Assert-True ($notApplicableChecks.Count -eq 6) 'architecture-specific NotApplicable checks were not preserved.'
    Assert-True (@($notApplicableChecks | Where-Object {
        $null -ne $_.PSObject.Properties['score'] -or $null -ne $_.PSObject.Properties['passed']
    }).Count -eq 0) 'NotApplicable checks acquired a numeric score or verdict.'

    $unmeasuredInput = Join-Path $testRoot 'eval03-unmeasured-private.json'
    $unmeasuredJson = Join-Path $testRoot 'eval03-unmeasured-public.json'
    $unmeasuredHtml = Join-Path $testRoot 'eval03-unmeasured-public.html'
    [IO.File]::WriteAllText($unmeasuredInput, ((New-UnmeasuredEval03Fixture) | ConvertTo-Json -Depth 100), $encoding)
    & $exporter -InputPath $unmeasuredInput -JsonPath $unmeasuredJson -HtmlPath $unmeasuredHtml
    $unmeasuredJsonText = [IO.File]::ReadAllText($unmeasuredJson)
    $unmeasuredHtmlText = [IO.File]::ReadAllText($unmeasuredHtml)
    Assert-True (-not $unmeasuredJsonText.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'unmeasured Eval03 JSON leaked a private sentinel.'
    Assert-True (-not $unmeasuredHtmlText.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'unmeasured Eval03 HTML leaked a private sentinel.'
    Assert-True ($unmeasuredHtmlText.Contains('not measured', [StringComparison]::Ordinal)) 'unmeasured Eval03 HTML did not render missingness explicitly.'
    $publicUnmeasured = $unmeasuredJsonText | ConvertFrom-Json -Depth 100
    $missingTrial = @($publicUnmeasured.trials | Where-Object measurement -ceq 'notMeasured')[0]
    Assert-True ($null -ne $missingTrial) 'unmeasured Eval03 trial was not preserved.'
    $missingChecks = @($missingTrial.checks | Where-Object measurement -ceq 'notMeasured')
    Assert-True ($missingChecks.Count -eq 3) 'unmeasured Eval03 checks were not preserved.'
    Assert-True (@($missingChecks | Where-Object {
        $null -ne $_.PSObject.Properties['score'] -or $null -ne $_.PSObject.Properties['passed']
    }).Count -eq 0) 'unmeasured checks acquired a numeric score or verdict.'
    Assert-True ($null -eq $missingTrial.criteria[0].PSObject.Properties['met']) 'an unmeasured criterion acquired a verdict.'

    foreach ($missingField in @('passed', 'score')) {
        $malformedInput = Join-Path $testRoot "malformed-$missingField-private.json"
        $malformedJson = Join-Path $testRoot "malformed-$missingField.json"
        $malformedHtml = Join-Path $testRoot "malformed-$missingField.html"
        $malformed = New-Eval03Fixture
        $malformed.trials[0].checks[0].Remove($missingField)
        [IO.File]::WriteAllText($malformedInput, ($malformed | ConvertTo-Json -Depth 100), $encoding)
        $malformedRejected = $false
        try {
            & $exporter -InputPath $malformedInput -JsonPath $malformedJson -HtmlPath $malformedHtml
        } catch {
            $malformedRejected = $_.Exception.Message.Contains('a measured check requires a score and verdict.', [StringComparison]::Ordinal)
        }
        Assert-True $malformedRejected "a measured check missing $missingField was not rejected by the typed contract."
        Assert-True (-not [IO.File]::Exists($malformedJson)) "a measured check missing $missingField produced JSON."
        Assert-True (-not [IO.File]::Exists($malformedHtml)) "a measured check missing $missingField produced HTML."
    }

    $eval06Input = Join-Path $testRoot 'eval06-private.json'
    $eval06Json = Join-Path $testRoot 'eval06-public.json'
    $eval06Html = Join-Path $testRoot 'eval06-public.html'
    [IO.File]::WriteAllText($eval06Input, ((New-Eval06Fixture) | ConvertTo-Json -Depth 100), $encoding)
    & $exporter -InputPath $eval06Input -JsonPath $eval06Json -HtmlPath $eval06Html
    $json06Text = [IO.File]::ReadAllText($eval06Json)
    $html06Text = [IO.File]::ReadAllText($eval06Html)
    Assert-True (-not $json06Text.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'Eval06 JSON leaked a private sentinel.'
    Assert-True (-not $html06Text.Contains('SENTINEL-PRIVATE-', [StringComparison]::Ordinal)) 'Eval06 HTML leaked a private sentinel.'
    $public06 = $json06Text | ConvertFrom-Json -Depth 100
    Assert-True ($public06.safety.probes.Count -eq 2) 'redacted safety probes were not preserved.'
    Assert-True ($public06.safety.probes[1].failure.stage -eq 'probe-execution') 'typed safety failure stage is missing.'
    Assert-True ($public06.safety.probes[1].failure.code -eq 'execution') 'typed safety failure code is missing.'
    Assert-True ($public06.failures[0].detail -eq 'The safety scan did not produce a complete, fully measured result.') 'free-form failure detail was not replaced by its canonical public diagnostic.'

    $unknownInput = Join-Path $testRoot 'unknown-private.json'
    $unknown = New-Eval03Fixture
    $unknown.unexpectedProviderPayload = 'SENTINEL-PRIVATE-UNKNOWN-FIELD'
    [IO.File]::WriteAllText($unknownInput, ($unknown | ConvertTo-Json -Depth 100), $encoding)
    $rejected = $false
    try {
        & $exporter -InputPath $unknownInput -JsonPath (Join-Path $testRoot 'unknown.json') -HtmlPath (Join-Path $testRoot 'unknown.html')
    } catch {
        $rejected = $_.Exception.Message.Contains("unknown property 'unexpectedProviderPayload'", [StringComparison]::Ordinal)
    }
    Assert-True $rejected 'an unknown top-level field was not rejected fail-closed.'
    Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'unknown.json'))) 'a rejected input produced JSON.'
    Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'unknown.html'))) 'a rejected input produced HTML.'

    $unsafeModelInput = Join-Path $testRoot 'unsafe-model-private.json'
    $unsafeModel = New-Eval03Fixture
    $unsafeModel.configuration.judgeModelId = 'sk-SENTINEL-PRIVATE-CREDENTIAL'
    [IO.File]::WriteAllText($unsafeModelInput, ($unsafeModel | ConvertTo-Json -Depth 100), $encoding)
    $unsafeModelRejected = $false
    try {
        & $exporter -InputPath $unsafeModelInput -JsonPath (Join-Path $testRoot 'unsafe-model.json') -HtmlPath (Join-Path $testRoot 'unsafe-model.html')
    } catch {
        $unsafeModelRejected = $_.Exception.Message.Contains('resembles credential material', [StringComparison]::Ordinal)
    }
    Assert-True $unsafeModelRejected 'a credential-shaped private model identifier was not rejected.'
    Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'unsafe-model.json'))) 'a credential-shaped private model identifier produced JSON.'
    Assert-True (-not [IO.File]::Exists((Join-Path $testRoot 'unsafe-model.html'))) 'a credential-shaped private model identifier produced HTML.'

    Write-Host 'Live-evidence exporter self-test: PASS (Eval03 N/A and missingness, malformed measured checks, Eval06, repeatability, deployment-sentinel exclusion, HTML escaping, unknown-field and unsafe-model rejection).'
} finally {
    $resolvedForDelete = [IO.Path]::GetFullPath($testRoot) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedForDelete.StartsWith($engRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing to remove a temporary path outside eng/.'
    }
    if ([IO.Directory]::Exists($testRoot)) { [IO.Directory]::Delete($testRoot, $true) }
}
