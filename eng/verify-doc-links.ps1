# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Fail([string] $Message) {
    throw "Documentation link verification failed: $Message"
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$privateDocDirectories = @('plans', 'reviews', 'stratGal')

Push-Location $root
try {
    $trackedDocumentation = @(git ls-files -- '*.md' '*.html')
    if ($LASTEXITCODE -ne 0) { Fail 'git ls-files failed.' }

    $documentation = @($trackedDocumentation | Where-Object {
        $normalized = $_.Replace('\', '/')
        $segments = $normalized.Split('/')
        -not ($segments.Count -gt 1 -and
            $segments[0].Equals('docs', [StringComparison]::OrdinalIgnoreCase) -and
            $privateDocDirectories -contains $segments[1])
    })
    if ($documentation.Count -eq 0) { Fail 'no tracked Markdown or HTML documents were found.' }

    $failures = [Collections.Generic.List[string]]::new()
    foreach ($relativeSource in $documentation) {
        $source = Join-Path $root $relativeSource
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            $failures.Add("${relativeSource}: tracked document is missing")
            continue
        }

        $content = [IO.File]::ReadAllText($source)
        $targets = [Collections.Generic.List[string]]::new()
        foreach ($match in [regex]::Matches($content, '(?i)(?:href|src)\s*=\s*["''](?<target>[^"'']+)["'']')) {
            $targets.Add($match.Groups['target'].Value)
        }
        foreach ($match in [regex]::Matches($content, '!?(?:\[[^\]]*\])\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^"'']*["''])?\)')) {
            $targets.Add($match.Groups['target'].Value)
        }

        foreach ($rawTarget in $targets) {
            $target = [Net.WebUtility]::HtmlDecode($rawTarget.Trim())
            if ($target.StartsWith('<') -and $target.EndsWith('>')) {
                $target = $target.Substring(1, $target.Length - 2)
            }
            if ([string]::IsNullOrWhiteSpace($target) -or $target.StartsWith('#')) { continue }
            if ($target -match '^[A-Za-z][A-Za-z0-9+.-]*:') { continue }

            $pathPart = ($target -split '[?#]', 2)[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) { continue }
            try {
                $pathPart = [Uri]::UnescapeDataString($pathPart)
                $resolved = if ($pathPart.StartsWith('/')) {
                    [IO.Path]::GetFullPath((Join-Path $root $pathPart.TrimStart('/')))
                }
                else {
                    [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $source) $pathPart))
                }
            }
            catch {
                $failures.Add("$relativeSource -> $rawTarget (invalid path)")
                continue
            }

            if (-not ($resolved.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
                $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase))) {
                $failures.Add("$relativeSource -> $rawTarget (escapes repository)")
                continue
            }
            if (-not (Test-Path -LiteralPath $resolved)) {
                $failures.Add("$relativeSource -> $rawTarget (target not found)")
            }
        }
    }

    if ($failures.Count -gt 0) {
        Fail (($failures | Sort-Object -Unique) -join [Environment]::NewLine)
    }

    Write-Host "Documentation links verified across $($documentation.Count) tracked Markdown/HTML files."
}
finally {
    Pop-Location
}
