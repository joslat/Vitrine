# SPDX-License-Identifier: MIT
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

function Fail([string]$Message) {
    throw "Publication verification failed: $Message"
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $root
try {
    $tracked = @(git ls-files)
    if ($LASTEXITCODE -ne 0) { Fail 'git ls-files failed.' }

    $forbiddenPrefixes = @('docs/plans/', 'docs/reviews/', 'docs/stratGal/')
    foreach ($path in $tracked) {
        $normalized = $path.Replace('\', '/')
        if ($forbiddenPrefixes | Where-Object { $normalized.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }) {
            Fail "private-history path is tracked: $path"
        }
    }

    $required = @(
        'LICENSE', 'NOTICE.md', 'DATA-PROVENANCE.md', 'SECURITY.md', 'CONTRIBUTING.md',
        'THIRD-PARTY-NOTICES.md', 'docs/index.html',
        'docs/evidence/vitrine-synthetic-live-2026-09-04-eval02b-02c.html'
    )
    foreach ($path in $required) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "required file is missing: $path" }
    }

    $textExtensions = @('.cs', '.csproj', '.props', '.slnx', '.ps1', '.cmd', '.md', '.html', '.css', '.json', '.yml', '.yaml', '.txt')
    $needles = @(
        'C:\git\joslat', 'C:\\git\\joslat', '/Users/jos', '/home/jos',
        'msfoundryjose', 'docs/stratGal', 'Galaxus Interview Demo'
    )
    foreach ($path in $tracked) {
        if ($textExtensions -notcontains [IO.Path]::GetExtension($path)) { continue }
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
        if ($textExtensions -notcontains [IO.Path]::GetExtension($path)) { continue }
        if ($path -eq 'eng/verify-publication.ps1') { continue }
        $content = [IO.File]::ReadAllText((Join-Path $root $path))
        foreach ($pattern in $secretPatterns) {
            foreach ($match in [regex]::Matches($content, $pattern)) {
                if ($match.Value.Contains('SENTINEL-', [StringComparison]::Ordinal)) { continue }
                Fail "possible secret or endpoint found in $path"
            }
        }
    }

    Write-Host "Publication boundary verified: $($tracked.Count) tracked files."
}
finally {
    Pop-Location
}
