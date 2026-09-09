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

if ([string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_ENDPOINT) -or
    [string]::IsNullOrWhiteSpace($env:AZURE_OPENAI_API_KEY)) {
    throw "Live tests were opted in, but AZURE_OPENAI_ENDPOINT and AZURE_OPENAI_API_KEY are required. Values are never reported."
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
