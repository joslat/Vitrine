# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $NoRestore,

    [ValidateRange(1, 120)]
    [int] $TimeoutMinutes = 15
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ($env:VITRINE_RUN_LIVE_MODEL_TESTS -cne "1") {
    throw "Live model tests are disabled. Set VITRINE_RUN_LIVE_MODEL_TESTS=1 to acknowledge model calls and possible charges."
}

if ([string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_ENDPOINT)) {
    throw "Live tests were opted in, but AZURE_OPENAI_ENDPOINT is required. Its value is never reported."
}

$endpointUri = $null
if (-not [Uri]::TryCreate($env:AZURE_OPENAI_ENDPOINT.Trim(), [UriKind]::Absolute, [ref] $endpointUri) -or
    $endpointUri.Scheme -ne "https") {
    throw "AZURE_OPENAI_ENDPOINT must be an absolute HTTPS Azure OpenAI resource endpoint. Its value is never reported."
}
$endpointPath = [Uri]::UnescapeDataString($endpointUri.AbsolutePath).TrimEnd('/')
if ($endpointPath -ieq '/api/projects' -or $endpointPath -imatch '/api/projects/') {
    throw "AZURE_OPENAI_ENDPOINT must be an Azure OpenAI resource/inference endpoint, not a Microsoft Foundry project endpoint. Its value is never reported."
}
if ($endpointUri.AbsolutePath -ne '/' -or -not [string]::IsNullOrEmpty($endpointUri.Query) -or
    -not [string]::IsNullOrEmpty($endpointUri.Fragment) -or -not [string]::IsNullOrEmpty($endpointUri.UserInfo)) {
    throw "AZURE_OPENAI_ENDPOINT must be the base Azure OpenAI resource/inference endpoint without a path, query, fragment, or user information. Its value is never reported."
}

$authMode = if ([string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_AUTH_MODE)) {
    "api-key"
} else {
    $env:AZURE_OPENAI_AUTH_MODE.Trim().ToLowerInvariant()
}

switch ($authMode) {
    "api-key" {
        if ([string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_API_KEY)) {
            throw "AZURE_OPENAI_API_KEY is required for api-key authentication. Its value is never reported."
        }
    }
    "default-credential" { }
    "managed-identity" {
        if (-not [string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID)) {
            $managedIdentityClientId = [Guid]::Empty
            if (-not [Guid]::TryParse(
                $env:AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID.Trim(),
                [ref] $managedIdentityClientId)) {
                throw "AZURE_OPENAI_MANAGED_IDENTITY_CLIENT_ID must be a user-assigned identity client GUID. Its value is never reported."
            }
        }
    }
    default {
        throw "AZURE_OPENAI_AUTH_MODE must be api-key, default-credential, or managed-identity."
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "AgentEval.VitrineDemo.slnx"

Push-Location $repositoryRoot
try {
    if (-not $NoRestore) {
        & dotnet restore $solutionPath
        if ($LASTEXITCODE -ne 0) { throw "Package restore failed with exit code $LASTEXITCODE." }
    }

    $discovery = @(& dotnet test $solutionPath --configuration $Configuration --no-restore --list-tests --filter "Category=LiveModel")
    if ($LASTEXITCODE -ne 0) { throw "Live-model test discovery failed with exit code $LASTEXITCODE." }
    $liveTests = @($discovery | Where-Object { $_ -match '^\s+AgentEval[.]VitrineDemo[.]Tests[.]' })
    $expectedLiveTests = 2
    if ($liveTests.Count -ne $expectedLiveTests) {
        throw "Expected $expectedLiveTests live-model contracts, but discovery found $($liveTests.Count). No model call was started."
    }

    Write-Host "Discovered $($liveTests.Count) live-model contracts. Total timeout: $TimeoutMinutes minute(s)."
    $arguments = @(
        "test", $solutionPath,
        "--configuration", $Configuration,
        "--no-restore",
        "--filter", "Category=LiveModel"
    )
    $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw "Live-model contract tests exceeded the $TimeoutMinutes-minute total timeout and were terminated."
    }
    if ($process.ExitCode -ne 0) { throw "Live-model contract tests failed with exit code $($process.ExitCode)." }
    Write-Host "All $($liveTests.Count) live-model contracts passed."
}
finally {
    Pop-Location
}
