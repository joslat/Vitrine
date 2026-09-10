# SPDX-License-Identifier: MIT
#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Fail([string]$Message) {
    throw "Publication verification failed: $Message"
}

function Embedded-Policy {
    # The public snapshot deliberately excludes docs/plans. Keep this mirror in the public verifier,
    # and require it to agree with the private source policy whenever that policy is available.
    return [ordered]@{
        schemaVersion = 1
        mode = 'deny-by-default'
        publicRootFiles = @(
            '.gitattributes', '.gitleaks.toml', '.gitignore', 'AgentEval.VitrineDemo.slnx',
            'AUTHORS.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'DATA-PROVENANCE.md',
            'Directory.Build.props', 'Directory.Packages.props', 'LICENSE', 'MIGRATION.md',
            'NOTICE.md', 'README.md', 'SECURITY.md', 'THIRD-PARTY-NOTICES.md', 'global.json',
            'start.cmd', 'start.ps1'
        )
        publicPrefixes = @(
            '.github/', 'eng/', 'src/', 'tests/', 'docs/assets/', 'docs/evidence/',
            'docs/images/', 'docs/reports/'
        )
        publicDocFiles = @(
            'docs/.nojekyll', 'docs/README.md', 'docs/SCORING-RUBRIC.md', 'docs/index.html',
            'docs/Vitrine-Architecture.html', 'docs/Vitrine-Digitec-Galaxus-Value-Proposal.html',
            'docs/Vitrine-Evaluation-Protocol.html', 'docs/Vitrine-Live-Run-Setup.html',
            'docs/Vitrine-One-Page-Summary.html',
            'docs/Vitrine-Scorecard-2026-09-10.md',
            'docs/Vitrine-Scorecard-2026-09-10-narrow-iteration.md',
            'docs/Vitrine-Retrieval-Deep-Dive.html', 'docs/Vitrine-Verification.html',
            'docs/Vitrine-Walkthrough.html'
        )
        privatePrefixes = @('docs/plans/', 'docs/reviews/', 'docs/stratGal/')
    }
}

function Policy-Array([object]$Policy, [string]$Name) {
    $property = $Policy.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        Fail "publication policy is missing '$Name'."
    }
    $values = @($property.Value)
    foreach ($value in $values) {
        if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
            Fail "publication policy '$Name' contains a blank or non-string entry."
        }
    }
    return [string[]]$values
}

function Normalize-PolicyEntry([string]$Value, [string]$Collection, [bool]$IsPrefix) {
    if ($Value.Contains('\')) { Fail "publication policy '$Collection' must use '/' separators: $Value" }
    if ([IO.Path]::IsPathRooted($Value) -or $Value.StartsWith('/', [StringComparison]::Ordinal)) {
        Fail "publication policy '$Collection' contains an absolute path: $Value"
    }
    $segments = $Value.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
    if ($segments.Count -eq 0 -or $segments | Where-Object { $_ -in @('.', '..') }) {
        Fail "publication policy '$Collection' contains a non-canonical path: $Value"
    }
    if ($IsPrefix -and -not $Value.EndsWith('/', [StringComparison]::Ordinal)) {
        Fail "publication policy prefix must end in '/': $Value"
    }
    if (-not $IsPrefix -and $Value.EndsWith('/', [StringComparison]::Ordinal)) {
        Fail "publication policy file entry must not end in '/': $Value"
    }
    return $Value
}

function Policy-Descriptors([object]$Policy) {
    $knownProperties = @('schemaVersion', 'mode', 'publicRootFiles', 'publicPrefixes',
        'publicDocFiles', 'privatePrefixes', 'notes')
    foreach ($property in $Policy.PSObject.Properties.Name) {
        if ($knownProperties -cnotcontains $property) {
            Fail "publication policy contains unknown property '$property'."
        }
    }
    if ($Policy.schemaVersion -ne 1) { Fail "unsupported publication policy schema '$($Policy.schemaVersion)'." }
    if ($Policy.mode -cne 'deny-by-default') { Fail "publication policy mode must be 'deny-by-default'." }

    $descriptors = [Collections.Generic.List[object]]::new()
    foreach ($definition in @(
        @{ Name = 'publicRootFiles'; Prefix = $false; Disposition = 'public' },
        @{ Name = 'publicPrefixes'; Prefix = $true; Disposition = 'public' },
        @{ Name = 'publicDocFiles'; Prefix = $false; Disposition = 'public' },
        @{ Name = 'privatePrefixes'; Prefix = $true; Disposition = 'private' }
    )) {
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($raw in (Policy-Array $Policy $definition.Name)) {
            $entry = Normalize-PolicyEntry $raw $definition.Name $definition.Prefix
            if (-not $seen.Add($entry)) { Fail "publication policy '$($definition.Name)' repeats '$entry'." }
            $descriptors.Add([pscustomobject]@{
                Collection = $definition.Name
                Value = $entry
                IsPrefix = [bool]$definition.Prefix
                Disposition = $definition.Disposition
            })
        }
    }

    for ($left = 0; $left -lt $descriptors.Count; $left++) {
        for ($right = $left + 1; $right -lt $descriptors.Count; $right++) {
            $a = $descriptors[$left]
            $b = $descriptors[$right]
            $overlap = if (-not $a.IsPrefix -and -not $b.IsPrefix) {
                $a.Value -ceq $b.Value
            } elseif ($a.IsPrefix -and $b.IsPrefix) {
                $a.Value.StartsWith($b.Value, [StringComparison]::Ordinal) -or
                    $b.Value.StartsWith($a.Value, [StringComparison]::Ordinal)
            } elseif ($a.IsPrefix) {
                $b.Value.StartsWith($a.Value, [StringComparison]::Ordinal)
            } else {
                $a.Value.StartsWith($b.Value, [StringComparison]::Ordinal)
            }
            if ($overlap) {
                Fail "publication policy overlaps '$($a.Collection):$($a.Value)' and '$($b.Collection):$($b.Value)'."
            }
        }
    }
    return $descriptors.ToArray()
}

function Classify-Path([string]$Path, [object[]]$Descriptors) {
    $matches = @($Descriptors | Where-Object {
        if ($_.IsPrefix) { $Path.StartsWith($_.Value, [StringComparison]::Ordinal) }
        else { $Path -ceq $_.Value }
    })
    if ($matches.Count -eq 0) { return [pscustomobject]@{ State = 'unclassified'; Matches = @() } }
    if ($matches.Count -gt 1) { return [pscustomobject]@{ State = 'overlap'; Matches = $matches } }
    return [pscustomobject]@{ State = $matches[0].Disposition; Matches = $matches }
}

function Canonical-PolicyJson([object]$Policy) {
    $canonical = [ordered]@{
        schemaVersion = [int]$Policy.schemaVersion
        mode = [string]$Policy.mode
        publicRootFiles = @(Policy-Array $Policy 'publicRootFiles')
        publicPrefixes = @(Policy-Array $Policy 'publicPrefixes')
        publicDocFiles = @(Policy-Array $Policy 'publicDocFiles')
        privatePrefixes = @(Policy-Array $Policy 'privatePrefixes')
    }
    return ($canonical | ConvertTo-Json -Depth 4 -Compress)
}

function Assert-PolicySelfTest {
    $policy = [pscustomobject](Embedded-Policy)
    $descriptors = @(Policy-Descriptors $policy)
    $cases = @(
        @{ Path = 'README.md'; State = 'public' },
        @{ Path = '.github/workflows/ci.yml'; State = 'public' },
        @{ Path = 'docs/index.html'; State = 'public' },
        @{ Path = 'docs/images/vitrine-customer-recommendation.png'; State = 'public' },
        @{ Path = 'docs/plans/todo.md'; State = 'private' },
        @{ Path = 'surprise.txt'; State = 'unclassified' }
    )
    foreach ($case in $cases) {
        $actual = (Classify-Path $case.Path $descriptors).State
        if ($actual -cne $case.State) {
            throw "Publication verifier self-test failed for '$($case.Path)': expected $($case.State), observed $actual."
        }
    }

    $overlapRejected = $false
    try {
        $broken = Embedded-Policy
        $broken.publicPrefixes = @($broken.publicPrefixes) + @('docs/')
        [void](Policy-Descriptors ([pscustomobject]$broken))
    } catch {
        $overlapRejected = $_.Exception.Message.Contains('overlaps', [StringComparison]::Ordinal)
    }
    if (-not $overlapRejected) { throw 'Publication verifier self-test failed to reject an overlapping policy.' }
    Write-Host 'Publication verifier self-test: PASS (public, private, unknown, and overlap classifications).'
}

if ($SelfTest) {
    Assert-PolicySelfTest
    exit 0
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$embeddedPolicy = [pscustomobject](Embedded-Policy)
$policyPath = Join-Path $root 'docs/plans/publication-path-policy.json'
if (Test-Path -LiteralPath $policyPath -PathType Leaf) {
    try {
        $sourcePolicy = Get-Content -Raw -LiteralPath $policyPath | ConvertFrom-Json -Depth 20
    } catch {
        Fail "publication policy is not valid JSON ($($_.Exception.GetType().Name))."
    }
    [void](Policy-Descriptors $sourcePolicy)
    if ((Canonical-PolicyJson $sourcePolicy) -cne (Canonical-PolicyJson $embeddedPolicy)) {
        Fail 'private source policy and embedded public policy differ; update both in one reviewed change.'
    }
}
$descriptors = @(Policy-Descriptors $embeddedPolicy)

Push-Location $root
try {
    $tracked = @(git ls-files)
    if ($LASTEXITCODE -ne 0) { Fail 'git ls-files failed.' }
    if ($tracked.Count -eq 0) { Fail 'the snapshot contains no tracked files.' }

    $trackedSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($rawPath in $tracked) {
        $path = $rawPath.Replace('\', '/')
        if (-not $trackedSet.Add($path)) { Fail "tracked path is duplicated after normalization: $path" }
        $classification = Classify-Path $path $descriptors
        switch ($classification.State) {
            'unclassified' { Fail "tracked path is not classified by the deny-by-default policy: $path" }
            'overlap' {
                $names = $classification.Matches | ForEach-Object { "$($_.Collection):$($_.Value)" }
                Fail "tracked path matches multiple policy entries ($($names -join ', ')): $path"
            }
            'private' { Fail "excluded/private path is tracked in the public snapshot: $path" }
            'public' { }
            default { Fail "tracked path has unknown policy disposition '$($classification.State)': $path" }
        }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Fail "tracked public file is missing from the working tree: $path"
        }
    }

    $stageLines = @(git ls-files --stage)
    if ($LASTEXITCODE -ne 0) { Fail 'git ls-files --stage failed.' }
    foreach ($line in $stageLines) {
        if ($line -notmatch '^(?<mode>[0-9]{6}) [0-9a-f]+ [0-3]\t(?<path>.+)$') {
            Fail 'could not parse a tracked index entry.'
        }
        if ($Matches.mode -notin @('100644', '100755')) {
            Fail "non-regular tracked entry is forbidden in the public snapshot: $($Matches.path) (mode $($Matches.mode))"
        }
    }

    $required = @(
        'README.md', 'LICENSE', 'NOTICE.md', 'DATA-PROVENANCE.md', 'SECURITY.md',
        'CONTRIBUTING.md', 'THIRD-PARTY-NOTICES.md', 'docs/index.html',
        'docs/Vitrine-One-Page-Summary.html', 'docs/images/vitrine-control-room.png',
        'docs/images/vitrine-customer-recommendation.png', 'docs/reports/demo01-scripted.html',
        'docs/reports/evals-offline.html', 'docs/reports/evals-offline.json',
        'docs/evidence/vitrine-synthetic-live-2026-09-04-eval02b-02c.html'
    )
    foreach ($path in $required) {
        if (-not $trackedSet.Contains($path)) { Fail "required public file is not tracked: $path" }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "required file is missing: $path" }
    }

    $pngSignature = [byte[]](137, 80, 78, 71, 13, 10, 26, 10)
    foreach ($path in @('docs/images/vitrine-control-room.png', 'docs/images/vitrine-customer-recommendation.png')) {
        $bytes = [IO.File]::ReadAllBytes((Join-Path $root $path))
        if ($bytes.Length -le $pngSignature.Length) { Fail "required PNG is empty or truncated: $path" }
        for ($index = 0; $index -lt $pngSignature.Length; $index++) {
            if ($bytes[$index] -ne $pngSignature[$index]) { Fail "required image is not a PNG: $path" }
        }
    }

    $textExtensions = @('.cs', '.csproj', '.props', '.slnx', '.ps1', '.cmd', '.md', '.html',
        '.css', '.json', '.yml', '.yaml', '.txt', '.axaml', '.svg', '.toml')
    $extensionlessTextFiles = @('CODEOWNERS', 'LICENSE')
    $needles = @(
        'C:\git\joslat', 'C:\\git\\joslat', '/Users/jos', '/home/jos',
        'msfoundryjose', 'docs/stratGal', 'Galaxus Interview Demo'
    )
    foreach ($path in $tracked) {
        $isText = $textExtensions -contains [IO.Path]::GetExtension($path) -or
            $extensionlessTextFiles -contains [IO.Path]::GetFileName($path)
        if (-not $isText) { continue }
        if ($path -eq 'eng/verify-publication.ps1') { continue }
        $content = [IO.File]::ReadAllText((Join-Path $root $path))
        foreach ($needle in $needles) {
            if ($content.Contains($needle, [StringComparison]::OrdinalIgnoreCase)) {
                Fail "forbidden publication text '$needle' found in $path"
            }
        }
    }

    $secretPatterns = @(
        '(?i)-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----',
        '(?i)(api[_-]?key|client[_-]?secret|access[_-]?token)\s*[:=]\s*["''][^"'']{12,}["'']',
        'https://[^\s"'']+\.openai\.azure\.com'
    )
    foreach ($path in $tracked) {
        $isText = $textExtensions -contains [IO.Path]::GetExtension($path) -or
            $extensionlessTextFiles -contains [IO.Path]::GetFileName($path)
        if (-not $isText) { continue }
        if ($path -eq 'eng/verify-publication.ps1') { continue }
        $content = [IO.File]::ReadAllText((Join-Path $root $path))
        foreach ($pattern in $secretPatterns) {
            foreach ($match in [regex]::Matches($content, $pattern)) {
                if ($match.Value.Contains('SENTINEL-', [StringComparison]::Ordinal)) { continue }
                Fail "possible secret or endpoint found in $path"
            }
        }
    }

    Write-Host "Publication boundary verified: $($tracked.Count) tracked files; every path has exactly one public policy classification."
}
finally {
    Pop-Location
}
