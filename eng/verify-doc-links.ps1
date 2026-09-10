# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Fail([string] $Message) {
    throw "Documentation verification failed: $Message"
}

function Add-Failure(
    [Collections.Generic.List[string]] $Failures,
    [string] $Message) {
    $Failures.Add($Message)
}

function Get-AttributeValues(
    [string] $Content,
    [string] $AttributeName) {
    # HTML attributes are whitespace-delimited. Requiring that delimiter avoids
    # treating data-id or aria-labelledby as an id attribute.
    $pattern = '(?is)(?<!\S)' + [regex]::Escape($AttributeName) + '\s*=\s*(?:"(?<double>[^"]*)"|''(?<single>[^'']*)'')'
    foreach ($match in [regex]::Matches($Content, $pattern)) {
        if ($match.Groups['double'].Success) {
            $match.Groups['double'].Value
        }
        else {
            $match.Groups['single'].Value
        }
    }
}

function Test-StandaloneHtml(
    [string] $RelativePath,
    [string] $Content,
    [Collections.Generic.List[string]] $Failures) {
    # SPDX/license comments may precede the doctype; no rendered content may.
    $doctype = [regex]::Match($Content, '(?is)^\s*(?:<!--.*?-->\s*)*<!doctype\s+html\s*>')
    if (-not $doctype.Success) {
        Add-Failure $Failures "${RelativePath}: missing HTML5 doctype at the start of the document"
    }

    $elements = @{}
    foreach ($elementName in @('html', 'head', 'body')) {
        $openMatches = [regex]::Matches($Content, "(?is)<$elementName\b[^>]*>")
        $closeMatches = [regex]::Matches($Content, "(?is)</$elementName\s*>")
        if ($openMatches.Count -ne 1 -or $closeMatches.Count -ne 1) {
            Add-Failure $Failures "${RelativePath}: expected exactly one <$elementName>...</$elementName> envelope; found $($openMatches.Count) opening and $($closeMatches.Count) closing tags"
            continue
        }

        $elements[$elementName] = @($openMatches[0], $closeMatches[0])
    }

    if ($elements.Count -eq 3) {
        $htmlOpen = $elements['html'][0]
        $htmlClose = $elements['html'][1]
        $headOpen = $elements['head'][0]
        $headClose = $elements['head'][1]
        $bodyOpen = $elements['body'][0]
        $bodyClose = $elements['body'][1]
        $ordered = $htmlOpen.Index -lt $headOpen.Index -and
            $headOpen.Index -lt $headClose.Index -and
            $headClose.Index -lt $bodyOpen.Index -and
            $bodyOpen.Index -lt $bodyClose.Index -and
            $bodyClose.Index -lt $htmlClose.Index
        if (-not $ordered) {
            Add-Failure $Failures "${RelativePath}: <html>, <head>, and <body> are not in a valid standalone-document order"
        }

        $htmlOpenText = $htmlOpen.Value
        if ($htmlOpenText -notmatch '(?is)\blang\s*=\s*["''][^"'']+["'']') {
            Add-Failure $Failures "${RelativePath}: <html> is missing a non-empty lang attribute"
        }

        $trailingStart = $htmlClose.Index + $htmlClose.Length
        if ($trailingStart -lt $Content.Length -and
            -not [string]::IsNullOrWhiteSpace($Content.Substring($trailingStart))) {
            Add-Failure $Failures "${RelativePath}: non-whitespace content appears after </html>"
        }

        $headStart = $headOpen.Index + $headOpen.Length
        $headLength = $headClose.Index - $headStart
        if ($headLength -ge 0) {
            $headContent = $Content.Substring($headStart, $headLength)
            $titleMatches = [regex]::Matches($headContent, '(?is)<title\b[^>]*>(?<value>.*?)</title\s*>')
            if ($titleMatches.Count -ne 1 -or
                [string]::IsNullOrWhiteSpace([Net.WebUtility]::HtmlDecode($titleMatches[0].Groups['value'].Value))) {
                Add-Failure $Failures "${RelativePath}: <head> must contain exactly one non-empty <title> element"
            }
            if ($headContent -notmatch '(?is)<meta\b[^>]*\bcharset\s*=\s*["'']?utf-8["'']?[^>]*>') {
                Add-Failure $Failures "${RelativePath}: <head> is missing a UTF-8 charset declaration"
            }
        }
    }
}

function Get-LinkTargets([string] $Content) {
    $targets = [Collections.Generic.List[string]]::new()

    foreach ($match in [regex]::Matches($Content, '(?is)(?<!\S)(?:href|src)\s*=\s*(?:"(?<double>[^"]*)"|''(?<single>[^'']*)'')')) {
        $value = if ($match.Groups['double'].Success) {
            $match.Groups['double'].Value
        }
        else {
            $match.Groups['single'].Value
        }
        $targets.Add($value)
    }

    foreach ($match in [regex]::Matches($Content, '!?(?:\[[^\]]*\])\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^"'']*["''])?\)')) {
        $targets.Add($match.Groups['target'].Value)
    }

    # Reference-style Markdown destinations: [reference]: path "optional title"
    foreach ($match in [regex]::Matches($Content, '(?m)^\s{0,3}\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)')) {
        $targets.Add($match.Groups['target'].Value)
    }

    $targets
}

function Get-HtmlTargetIndex(
    [string] $RelativePath,
    [string] $AbsolutePath,
    [string] $Content,
    [Collections.Generic.List[string]] $Failures) {
    Test-StandaloneHtml $RelativePath $Content $Failures

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($rawId in Get-AttributeValues $Content 'id') {
        $id = [Net.WebUtility]::HtmlDecode($rawId)
        if ([string]::IsNullOrWhiteSpace($id)) {
            Add-Failure $Failures "${RelativePath}: contains an empty id attribute"
            continue
        }
        if (-not $ids.Add($id)) {
            Add-Failure $Failures "${RelativePath}: duplicate id '$id'"
        }
    }

    # Legacy named anchors are valid fragment destinations, but are not IDs and
    # therefore do not participate in the duplicate-ID check above.
    $fragmentTargets = [Collections.Generic.HashSet[string]]::new($ids, [StringComparer]::Ordinal)
    foreach ($match in [regex]::Matches($Content, '(?is)<a\b[^>]*(?<!\S)name\s*=\s*(?:"(?<double>[^"]*)"|''(?<single>[^'']*)'')[^>]*>')) {
        $rawName = if ($match.Groups['double'].Success) {
            $match.Groups['double'].Value
        }
        else {
            $match.Groups['single'].Value
        }
        $name = [Net.WebUtility]::HtmlDecode($rawName)
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            [void]$fragmentTargets.Add($name)
        }
    }

    [pscustomobject]@{
        AbsolutePath = $AbsolutePath
        Targets = $fragmentTargets
    }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$privateDocDirectories = @('plans', 'reviews', 'stratGal')

Push-Location $root
try {
    # Include non-ignored working-tree documents so a newly added page is checked
    # before its first commit as well as in CI after it becomes tracked.
    $candidateDocumentation = @(git ls-files --cached --others --exclude-standard -- '*.md' '*.html')
    if ($LASTEXITCODE -ne 0) { Fail 'git ls-files failed.' }

    $documentation = @($candidateDocumentation | Where-Object {
        $normalized = $_.Replace('\', '/')
        $segments = $normalized.Split('/')
        -not ($segments.Count -gt 1 -and
            $segments[0].Equals('docs', [StringComparison]::OrdinalIgnoreCase) -and
            $privateDocDirectories -contains $segments[1])
    } | Sort-Object -Unique)
    if ($documentation.Count -eq 0) { Fail 'no Markdown or HTML documents were found.' }

    $failures = [Collections.Generic.List[string]]::new()
    $contentByPath = @{}
    $htmlTargetsByPath = @{}

    foreach ($relativeSource in $documentation) {
        $source = Join-Path $root $relativeSource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            Add-Failure $failures "${relativeSource}: document is missing"
            continue
        }

        $content = [IO.File]::ReadAllText($source)
        $absoluteSource = [IO.Path]::GetFullPath($source)
        $contentByPath[$absoluteSource] = $content
        if ([IO.Path]::GetExtension($source).Equals('.html', [StringComparison]::OrdinalIgnoreCase)) {
            $index = Get-HtmlTargetIndex $relativeSource $absoluteSource $content $failures
            $htmlTargetsByPath[$absoluteSource] = $index.Targets
        }
    }

    foreach ($relativeSource in $documentation) {
        $source = [IO.Path]::GetFullPath((Join-Path $root $relativeSource))
        if (-not $contentByPath.ContainsKey($source)) { continue }

        foreach ($rawTarget in Get-LinkTargets $contentByPath[$source]) {
            $target = [Net.WebUtility]::HtmlDecode($rawTarget.Trim())
            if ($target.StartsWith('<') -and $target.EndsWith('>')) {
                $target = $target.Substring(1, $target.Length - 2)
            }
            if ([string]::IsNullOrWhiteSpace($target)) { continue }
            if ($target.StartsWith('//')) { continue }
            if ($target -match '^[A-Za-z][A-Za-z0-9+.-]*:') { continue }

            $hashIndex = $target.IndexOf('#')
            $fragment = if ($hashIndex -ge 0) { $target.Substring($hashIndex + 1) } else { $null }
            $withoutFragment = if ($hashIndex -ge 0) { $target.Substring(0, $hashIndex) } else { $target }
            $pathPart = ($withoutFragment -split '\?', 2)[0]

            try {
                $decodedPath = [Uri]::UnescapeDataString($pathPart)
                $resolved = if ([string]::IsNullOrWhiteSpace($decodedPath)) {
                    $source
                }
                elseif ($decodedPath.StartsWith('/')) {
                    [IO.Path]::GetFullPath((Join-Path $root $decodedPath.TrimStart('/')))
                }
                else {
                    [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $source) $decodedPath))
                }
            }
            catch {
                Add-Failure $failures "$relativeSource -> $rawTarget (invalid path)"
                continue
            }

            if (-not ($resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
                $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase))) {
                Add-Failure $failures "$relativeSource -> $rawTarget (escapes repository)"
                continue
            }
            if (-not (Test-Path -LiteralPath $resolved)) {
                Add-Failure $failures "$relativeSource -> $rawTarget (target not found)"
                continue
            }

            if ($null -eq $fragment -or [string]::IsNullOrWhiteSpace($fragment)) { continue }
            if (-not [IO.Path]::GetExtension($resolved).Equals('.html', [StringComparison]::OrdinalIgnoreCase)) { continue }

            try {
                $decodedFragment = [Uri]::UnescapeDataString([Net.WebUtility]::HtmlDecode($fragment))
            }
            catch {
                Add-Failure $failures "$relativeSource -> $rawTarget (invalid fragment encoding)"
                continue
            }

            if (-not $htmlTargetsByPath.ContainsKey($resolved)) {
                $targetContent = [IO.File]::ReadAllText($resolved)
                $targetRelative = [IO.Path]::GetRelativePath($root, $resolved)
                $index = Get-HtmlTargetIndex $targetRelative $resolved $targetContent $failures
                $htmlTargetsByPath[$resolved] = $index.Targets
            }
            if (-not $htmlTargetsByPath[$resolved].Contains($decodedFragment)) {
                Add-Failure $failures "$relativeSource -> $rawTarget (fragment '#$decodedFragment' not found)"
            }
        }
    }

    if ($failures.Count -gt 0) {
        Fail (($failures | Sort-Object -Unique) -join [Environment]::NewLine)
    }

    Write-Host "Documentation verified across $($documentation.Count) Markdown/HTML files: local targets, HTML fragments, unique IDs, and standalone HTML envelopes are valid."
}
finally {
    Pop-Location
}
