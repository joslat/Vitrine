# SPDX-License-Identifier: MIT
[CmdletBinding()]
param(
    [ValidateSet("Demo01", "Demo02", "Evals", "Controls", "Ablation", "App")]
    [string] $Mode = "Demo01",

    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = $PSScriptRoot
$solutionPath = Join-Path $repositoryRoot "AgentEval.VitrineDemo.slnx"
$demoProject = Join-Path $repositoryRoot "src\AgentEval.VitrineDemo\AgentEval.VitrineDemo.csproj"
$evalProject = Join-Path $repositoryRoot "src\AgentEval.VitrineDemo.Evals\AgentEval.VitrineDemo.Evals.csproj"
$appProject = Join-Path $repositoryRoot "src\AgentEval.VitrineDemo.App\AgentEval.VitrineDemo.App.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found. Install .NET 10 from https://dotnet.microsoft.com/download/dotnet/10.0"
}

Push-Location $repositoryRoot
try {
    Write-Host "VITRINE · standalone AgentEval showcase" -ForegroundColor Cyan
    Write-Host "Mode: $Mode · Configuration: $Configuration"
    Write-Host "Offline by default. Live credentials are optional and are never printed; deployment name only."

    if (-not $NoRestore) {
        & dotnet restore $solutionPath
        if ($LASTEXITCODE -ne 0) { throw "Package restore failed with exit code $LASTEXITCODE." }
    }

    $project = $demoProject
    $runArguments = @()
    switch ($Mode) {
        "Demo01"  { $runArguments = @("1", "--offline") }
        "Demo02"  { $runArguments = @("2", "--offline") }
        "Evals"   { $project = $evalProject; $runArguments = @("--all") }
        "Controls" { $project = $evalProject; $runArguments = @("--controls") }
        "Ablation" { $project = $evalProject; $runArguments = @("--ablate-catalogue") }
        "App"      { $project = $appProject }
    }

    & dotnet run --project $project --configuration $Configuration --no-restore -- @runArguments
    if ($LASTEXITCODE -ne 0) {
        throw "VITRINE exited with code $LASTEXITCODE. Evaluation exit 1 means a mandatory-gate or registered-control failure, an admitted-check self-test failure, or expected catalogue-ablation detection; a matched-quality diagnostic finding alone cannot set it. Other exits: invalid arguments 2, NOT MEASURED 3, infrastructure failure 4, cancellation 130."
    }
}
finally {
    Pop-Location
}
